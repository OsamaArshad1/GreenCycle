using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var apiRoot = FindApiRoot();
var allowedCorsOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? [];

builder.Services.AddSingleton(new UserStore(apiRoot));
builder.Services.AddSingleton<OtpStore>();
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.Configure<WhatsAppOptions>(builder.Configuration.GetSection("WhatsApp"));
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddHttpClient<IWhatsAppSender, WhatsAppCloudSender>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .SetIsOriginAllowed(origin =>
            {
                if (string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (allowedCorsOrigins.Any(allowedOrigin =>
                    string.Equals(allowedOrigin.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
                    (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase));
            })
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();
var siteRoot = FindSiteRoot(apiRoot);
await app.Services.GetRequiredService<UserStore>().InitializeAsync();

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
        }

        return Task.CompletedTask;
    });

    await next();
});

app.UseDefaultFiles(new DefaultFilesOptions
{
    FileProvider = new PhysicalFileProvider(siteRoot)
});
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(siteRoot)
});
app.UseCors();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/waste/request-otp", async (WasteOtpRequest request, OtpStore otpStore, IEmailSender emailSender, IWhatsAppSender whatsAppSender) =>
{
    if (string.IsNullOrWhiteSpace(request.Phone))
    {
        return Results.BadRequest(new ApiError("Phone number is required."));
    }

    var channel = NormalizeOtpChannel(request.Channel);

    if (channel == OtpDeliveryChannel.Email && string.IsNullOrWhiteSpace(request.Email))
    {
        return Results.BadRequest(new ApiError("Email is required so the OTP can be sent securely."));
    }

    var challenge = otpStore.Create(request.Phone);

    if (channel == OtpDeliveryChannel.WhatsApp)
    {
        var whatsAppResult = await whatsAppSender.SendOtpAsync(request.Phone, challenge.Code);
        if (!whatsAppResult.Success)
        {
            return Results.Json(
                new ApiError(whatsAppResult.ErrorMessage ?? "WhatsApp OTP is not configured on the server."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new OtpChallengeResponse(
            MaskPhone(request.Phone),
            MaskPhone(request.Phone),
            "OTP sent to the entered WhatsApp phone number. Use the code to verify this waste submission.",
            null));
    }

    var emailResult = await emailSender.SendOtpAsync(request.Email!, challenge.Code);
    if (!emailResult.Success)
    {
        return Results.Json(
            new ApiError(emailResult.ErrorMessage ?? "Email OTP is not configured on the server."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new OtpChallengeResponse(
        request.Email!,
        null,
        "OTP sent to the entered email address. Use the code to verify this waste submission.",
        null));
});

app.MapPost("/api/waste/verify-otp", (WasteOtpVerifyRequest request, OtpStore otpStore) =>
{
    if (string.IsNullOrWhiteSpace(request.Phone) || string.IsNullOrWhiteSpace(request.Otp))
    {
        return Results.BadRequest(new ApiError("Phone number and OTP are required."));
    }

    if (!otpStore.Verify(request.Phone, request.Otp))
    {
        return Results.BadRequest(new ApiError("Invalid or expired OTP."));
    }

    return Results.Ok(new WasteOtpVerifiedResponse(
        request.Phone.Trim(),
        otpStore.CreateVerifiedToken(request.Phone),
        "OTP verified. You can submit the waste form."));
});

app.MapPost("/api/waste/submissions", async (HttpRequest request, UserStore store, OtpStore otpStore, IEmailSender emailSender) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new ApiError("Waste submission must be sent as form data."));
    }

    var form = await request.ReadFormAsync();
    var submitterType = GetRequired(form, "submitterType");
    var phone = GetRequired(form, "phone");
    var categories = form["wasteCategories"]
        .Select(value => value?.Trim())
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var targetCompanyIds = form["targetCompanyIds"]
        .Select(value => Guid.TryParse(value, out var companyId) ? companyId : Guid.Empty)
        .Where(companyId => companyId != Guid.Empty)
        .Distinct()
        .ToArray();
    var targetScope = form["targetCompanyScope"].ToString();
    var wasteCategory = categories.Length > 0
        ? string.Join(", ", categories)
        : GetRequired(form, "wasteCategory");
    var verificationToken = GetRequired(form, "verificationToken");

    if (submitterType is null || phone is null || wasteCategory is null || verificationToken is null)
    {
        return Results.BadRequest(new ApiError("Submission type, phone number, waste category, and OTP verification are required."));
    }

    if (!otpStore.ConsumeVerifiedToken(phone, verificationToken))
    {
        return Results.BadRequest(new ApiError("Please verify a new email OTP before submitting waste."));
    }

    if (!string.Equals(targetScope, "all", StringComparison.OrdinalIgnoreCase) && targetCompanyIds.Length == 0)
    {
        return Results.BadRequest(new ApiError("Select at least one recycler company or choose send to all companies."));
    }

    var submissionId = Guid.NewGuid();
    var imageFiles = form.Files
        .Where(file => string.Equals(file.Name, "images", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    if (imageFiles.Length > 5)
    {
        return Results.BadRequest(new ApiError("A maximum of 5 waste images can be uploaded."));
    }

    var images = await SaveWasteImagesAsync(imageFiles, siteRoot, submissionId);

    var submission = new WasteSubmission(
        submissionId,
        submitterType.Trim(),
        EmptyToNull(form["submitterName"].ToString()),
        EmptyToNull(form["companyName"].ToString()),
        phone.Trim(),
        EmptyToNull(form["email"].ToString()),
        EmptyToNull(form["address"].ToString()),
        wasteCategory.Trim(),
        EmptyToNull(form["quantity"].ToString()),
        EmptyToNull(form["notes"].ToString()),
        verificationToken.Trim(),
        "received",
        DateTimeOffset.UtcNow,
        images,
        targetCompanyIds);

    var sessionUser = await store.CreateWasteSubmissionAsync(submission);
    var matchedCompanies = await store.GetMatchingCompaniesForSubmissionAsync(submission);
    foreach (var company in matchedCompanies)
    {
        await emailSender.SendNotificationAsync(
            company.Email,
            "New Green Cycle waste order available for bidding",
            $"""
            A new waste order is available for bidding.

            Waste category: {submission.WasteCategory}
            Submitter type: {submission.SubmitterType}
            Location: {submission.Address ?? "Not provided"}
            Notes: {submission.Notes ?? "Not provided"}

            Log in to your Green Cycle dashboard to place a bid.
            """);
    }
    var session = sessionUser is null
        ? null
        : new AuthSessionResponse(
            sessionUser.Id,
            sessionUser.CompanyName,
            sessionUser.Email,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            "Waste submission completed. You are now signed in.");

    return Results.Created($"/api/waste/submissions/{submission.Id}", new WasteSubmissionResponse(
        submission.Id,
        submission.SubmitterType,
        submission.Phone,
        submission.WasteCategory,
        submission.Status,
        "Waste submission received.",
        session));
});

app.MapGet("/api/waste/prefill", async (string? phone, UserStore store) =>
{
    if (string.IsNullOrWhiteSpace(phone))
    {
        return Results.BadRequest(new ApiError("Phone number is required."));
    }

    var submission = await store.GetLatestWasteSubmissionByPhoneAsync(phone);
    if (submission is null)
    {
        return Results.NotFound(new ApiError("No saved details were found for this phone number."));
    }

    return Results.Ok(new WastePrefillResponse(
        submission.SubmitterType,
        submission.SubmitterName,
        submission.CompanyName,
        submission.Phone,
        submission.Email,
        submission.Address,
        SplitWasteCategories(submission.WasteCategory),
        submission.Notes));
});

app.MapPost("/api/auth/signup", async (HttpRequest request, UserStore store) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new ApiError("Signup must be sent as form data."));
    }

    var form = await request.ReadFormAsync();
    var email = GetRequired(form, "email");
    var companyName = GetRequired(form, "companyName");
    var mobile = GetRequired(form, "mobile");
    var categories = form["wasteCategories"]
        .Select(value => value?.Trim())
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    if (email is null || companyName is null || mobile is null || categories.Length == 0)
    {
        return Results.BadRequest(new ApiError("Company name, mobile, email, and at least one waste category are required."));
    }

    var logo = form.Files.GetFile("logo");
    var signup = new CompanySignup(
        companyName,
        mobile,
        email,
        form["address"].ToString().Trim(),
        form["website"].ToString().Trim(),
        logo?.FileName,
        categories);

    var result = await store.CreateAsync(signup);
    return result switch
    {
        SignupResult.Created created => Results.Created($"/api/companies/{created.User.Id}", new AuthResponse(
            created.User.Id,
            created.User.CompanyName,
            created.User.Email,
            "Signup completed.")),
        SignupResult.Duplicate => Results.Conflict(new ApiError("An account with this email already exists.")),
        _ => Results.BadRequest(new ApiError("Signup could not be completed."))
    };
});

app.MapPost("/api/auth/request-otp", async (LoginRequest request, UserStore store, OtpStore otpStore, IEmailSender emailSender, IWhatsAppSender whatsAppSender) =>
{
    if (string.IsNullOrWhiteSpace(request.Email))
    {
        return Results.BadRequest(new ApiError("Email is required."));
    }

    var user = await store.FindByEmailAsync(request.Email);
    if (user is null)
    {
        return Results.NotFound(new ApiError("No account was found for this email."));
    }

    var challenge = otpStore.Create(user.Email);
    var channel = NormalizeOtpChannel(request.Channel);

    if (channel == OtpDeliveryChannel.WhatsApp)
    {
        if (string.IsNullOrWhiteSpace(user.Mobile))
        {
            return Results.BadRequest(new ApiError("This account does not have a mobile number for WhatsApp OTP."));
        }

        var whatsAppResult = await whatsAppSender.SendOtpAsync(user.Mobile, challenge.Code);
        if (!whatsAppResult.Success)
        {
            return Results.Json(
                new ApiError(whatsAppResult.ErrorMessage ?? "WhatsApp OTP is not configured on the server."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new OtpChallengeResponse(
            user.Email,
            MaskPhone(user.Mobile),
            "OTP sent to the registered WhatsApp phone number. Use the code to finish login.",
            null));
    }

    var emailResult = await emailSender.SendOtpAsync(user.Email, challenge.Code);
    if (!emailResult.Success)
    {
        return Results.Json(
            new ApiError(emailResult.ErrorMessage ?? "Email OTP is not configured on the server."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new OtpChallengeResponse(
        user.Email,
        null,
        "OTP sent to the registered email address. Use the code to finish login.",
        null));
});

app.MapPost("/api/auth/verify-otp", async (OtpVerifyRequest request, UserStore store, OtpStore otpStore) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Otp))
    {
        return Results.BadRequest(new ApiError("Email and OTP are required."));
    }

    var user = await store.FindByEmailAsync(request.Email);
    if (user is null)
    {
        return Results.NotFound(new ApiError("No account was found for this email."));
    }

    if (!otpStore.Verify(user.Email, request.Otp))
    {
        return Results.BadRequest(new ApiError("Invalid or expired OTP."));
    }

    return Results.Ok(new AuthSessionResponse(
        user.Id,
        user.CompanyName,
        user.Email,
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        "Login completed."));
});

app.MapPost("/api/auth/login", async (LoginRequest request, UserStore store, OtpStore otpStore, IEmailSender emailSender, IWhatsAppSender whatsAppSender) =>
{
    if (string.IsNullOrWhiteSpace(request.Email))
    {
        return Results.BadRequest(new ApiError("Email is required."));
    }

    var user = await store.FindByEmailAsync(request.Email);
    if (user is null)
    {
        return Results.NotFound(new ApiError("No account was found for this email."));
    }

    var challenge = otpStore.Create(user.Email);
    var channel = NormalizeOtpChannel(request.Channel);

    if (channel == OtpDeliveryChannel.WhatsApp)
    {
        if (string.IsNullOrWhiteSpace(user.Mobile))
        {
            return Results.BadRequest(new ApiError("This account does not have a mobile number for WhatsApp OTP."));
        }

        var whatsAppResult = await whatsAppSender.SendOtpAsync(user.Mobile, challenge.Code);
        if (!whatsAppResult.Success)
        {
            return Results.Json(
                new ApiError(whatsAppResult.ErrorMessage ?? "WhatsApp OTP is not configured on the server."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new OtpChallengeResponse(
            user.Email,
            MaskPhone(user.Mobile),
            "OTP sent to the registered WhatsApp phone number. Use the code to finish login.",
            null));
    }

    var emailResult = await emailSender.SendOtpAsync(user.Email, challenge.Code);
    if (!emailResult.Success)
    {
        return Results.Json(
            new ApiError(emailResult.ErrorMessage ?? "Email OTP is not configured on the server."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new OtpChallengeResponse(
        user.Email,
        null,
        "OTP sent to the registered email address. Use the code to finish login.",
        null));
});

app.MapGet("/api/companies", async (UserStore store) =>
{
    var companies = await store.GetAllAsync();
    return Results.Ok(companies.Select(company => new CompanySummary(
        company.Id,
        company.CompanyName,
        company.Mobile,
        company.Email,
        company.Address,
        company.Website,
        company.WasteCategories,
        company.CreatedAtUtc)));
});

app.MapGet("/api/companies/recyclers", async (UserStore store) =>
{
    var companies = await store.GetAllAsync();
    return Results.Ok(companies.Select(company => new CompanySummary(
        company.Id,
        company.CompanyName,
        company.Mobile,
        company.Email,
        company.Address,
        company.Website,
        company.WasteCategories,
        company.CreatedAtUtc)));
});

app.MapGet("/api/waste/submissions", async (string? email, UserStore store) =>
{
    if (string.IsNullOrWhiteSpace(email))
    {
        return Results.BadRequest(new ApiError("Email is required."));
    }

    var submissions = await store.GetWasteSubmissionsByEmailAsync(email);
    return Results.Ok(submissions.Select(submission => new WasteSubmissionSummary(
        submission.Id,
        submission.SubmitterType,
        submission.SubmitterName,
        submission.CompanyName,
        submission.Phone,
        submission.Email,
        submission.Address,
        submission.WasteCategory,
        submission.Notes,
        submission.Status,
        submission.CreatedAtUtc,
        submission.Images.Length,
        submission.Images.Select(ToImageSummary).ToArray())));
});

app.MapGet("/api/waste/opportunities", async (string? companyEmail, UserStore store) =>
{
    if (string.IsNullOrWhiteSpace(companyEmail))
    {
        return Results.BadRequest(new ApiError("Company email is required."));
    }

    var opportunities = await store.GetBidOpportunitiesAsync(companyEmail);
    return Results.Ok(opportunities.Select(item => new BidOpportunitySummary(
        item.Submission.Id,
        item.Submission.SubmitterType,
        item.Submission.SubmitterName,
        item.Submission.CompanyName,
        item.Submission.Phone,
        item.Submission.Email,
        item.Submission.Address,
        item.Submission.WasteCategory,
        item.Submission.Notes,
        item.Submission.Status,
        item.Submission.CreatedAtUtc,
        item.Submission.Images.Length,
        item.Submission.Images.Select(ToImageSummary).ToArray(),
        item.ExistingBid?.Id,
        item.ExistingBid?.Amount,
        item.ExistingBid?.PickupDate,
        item.ExistingBid?.Status)));
});

app.MapGet("/api/waste/company-orders", async (string? companyEmail, UserStore store) =>
{
    if (string.IsNullOrWhiteSpace(companyEmail))
    {
        return Results.BadRequest(new ApiError("Company email is required."));
    }

    var orders = await store.GetCompanyOrdersAsync(companyEmail);
    return Results.Ok(orders.Select(item => new CompanyOrderSummary(
        item.Submission.Id,
        item.Submission.SubmitterType,
        item.Submission.SubmitterName,
        item.Submission.CompanyName,
        item.Submission.Phone,
        item.Submission.Email,
        item.Submission.Address,
        item.Submission.WasteCategory,
        item.Submission.Notes,
        item.Submission.Status,
        item.Submission.CreatedAtUtc,
        item.Submission.Images.Length,
        item.Submission.Images.Select(ToImageSummary).ToArray(),
        BidToSummary(item.Bid))));
});

app.MapGet("/api/waste/my-bids", async (string? companyEmail, UserStore store) =>
{
    if (string.IsNullOrWhiteSpace(companyEmail))
    {
        return Results.BadRequest(new ApiError("Company email is required."));
    }

    var bids = await store.GetCompanyBidsAsync(companyEmail);
    return Results.Ok(bids.Select(item => new CompanyBidSummary(
        item.Submission.Id,
        item.Submission.SubmitterType,
        item.Submission.SubmitterName,
        item.Submission.CompanyName,
        item.Submission.Phone,
        item.Submission.Email,
        item.Submission.Address,
        item.Submission.WasteCategory,
        item.Submission.Notes,
        item.Submission.Status,
        item.Submission.CreatedAtUtc,
        item.Submission.Images.Length,
        item.Submission.Images.Select(ToImageSummary).ToArray(),
        BidToSummary(item.Bid))));
});

app.MapGet("/api/waste/submissions/{submissionId:guid}/bids", async (Guid submissionId, string? requesterEmail, UserStore store) =>
{
    if (string.IsNullOrWhiteSpace(requesterEmail))
    {
        return Results.BadRequest(new ApiError("Requester email is required."));
    }

    var bids = await store.GetBidsForSubmissionAsync(submissionId, requesterEmail);
    if (bids is null)
    {
        return Results.NotFound(new ApiError("Submission was not found for this account."));
    }

    return Results.Ok(bids.Select(BidToSummary));
});

app.MapPost("/api/waste/submissions/{submissionId:guid}/bids", async (Guid submissionId, BidCreateRequest request, UserStore store, IEmailSender emailSender) =>
{
    if (string.IsNullOrWhiteSpace(request.CompanyEmail))
    {
        return Results.BadRequest(new ApiError("Company email is required."));
    }

    if (!DateOnly.TryParse(request.PickupDate, out var pickupDate))
    {
        return Results.BadRequest(new ApiError("Pickup date is required."));
    }

    if (pickupDate < DateOnly.FromDateTime(DateTime.UtcNow))
    {
        return Results.BadRequest(new ApiError("Pickup date must be today or a future date."));
    }

    var result = await store.CreateWasteBidAsync(submissionId, request.CompanyEmail, pickupDate, request.Notes);
    return result switch
    {
        BidCreateResult.Created created => await NotifyAndReturnCreatedBid(created, emailSender),
        BidCreateResult.Duplicate => Results.Conflict(new ApiError("Your company already placed a bid for this order.")),
        BidCreateResult.NotFound => Results.NotFound(new ApiError("This waste order is not available for bidding.")),
        BidCreateResult.OwnSubmission => Results.BadRequest(new ApiError("You cannot bid on your own waste order.")),
        _ => Results.BadRequest(new ApiError("Bid could not be submitted."))
    };
});

app.MapPost("/api/waste/submissions/{submissionId:guid}/bids/{bidId:guid}/accept", async (Guid submissionId, Guid bidId, BidAcceptRequest request, UserStore store, IEmailSender emailSender) =>
{
    if (string.IsNullOrWhiteSpace(request.RequesterEmail))
    {
        return Results.BadRequest(new ApiError("Requester email is required."));
    }

    var result = await store.AcceptWasteBidAsync(submissionId, bidId, request.RequesterEmail);
    return result switch
    {
        BidAcceptResult.Accepted accepted => await NotifyAndReturnAcceptedBid(accepted, emailSender),
        BidAcceptResult.NotFound => Results.NotFound(new ApiError("Bid was not found for this waste order.")),
        BidAcceptResult.NotOwner => Results.BadRequest(new ApiError("Only the waste submitter can accept a bid.")),
        _ => Results.BadRequest(new ApiError("Bid could not be accepted."))
    };
});

app.MapPost("/api/waste/submissions/{submissionId:guid}/complete", async (Guid submissionId, CompleteSubmissionRequest request, UserStore store, IEmailSender emailSender) =>
{
    if (string.IsNullOrWhiteSpace(request.RequesterEmail))
    {
        return Results.BadRequest(new ApiError("Requester email is required."));
    }

    var result = await store.CompleteWasteSubmissionAsync(submissionId, request.RequesterEmail);
    return result switch
    {
        CompleteSubmissionResult.Completed completed => await NotifyAndReturnCompletedSubmission(completed, emailSender),
        CompleteSubmissionResult.NotFound => Results.NotFound(new ApiError("Submission was not found.")),
        CompleteSubmissionResult.NotOwner => Results.BadRequest(new ApiError("Only the waste submitter can complete this order.")),
        CompleteSubmissionResult.NoAcceptedBid => Results.BadRequest(new ApiError("This order needs an accepted bid before it can be completed.")),
        _ => Results.BadRequest(new ApiError("Order could not be completed."))
    };
});

app.MapGet("/api/company-reviews", async (string? companyEmail, UserStore store) =>
{
    if (string.IsNullOrWhiteSpace(companyEmail))
    {
        return Results.BadRequest(new ApiError("Company email is required."));
    }

    var reviews = await store.GetCompanyReviewsByEmailAsync(companyEmail);
    var averageRating = reviews.Count == 0
        ? 0
        : Math.Round(reviews.Average(review => review.Rating), 1);

    return Results.Ok(new CompanyReviewsResponse(
        averageRating,
        reviews.Count,
        reviews.Select(review => new CompanyReviewSummary(
            review.Id,
            review.ReviewerName,
            review.ReviewerEmail,
            review.Rating,
            review.Comment,
            review.CreatedAtUtc))));
});

app.MapPost("/api/company-reviews", async (CompanyReviewRequest request, UserStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.CompanyEmail) ||
        string.IsNullOrWhiteSpace(request.ReviewerName))
    {
        return Results.BadRequest(new ApiError("Company email and reviewer name are required."));
    }

    if (request.Rating is < 1 or > 5)
    {
        return Results.BadRequest(new ApiError("Rating must be between 1 and 5."));
    }

    var company = await store.FindByEmailAsync(request.CompanyEmail);
    if (company is null)
    {
        return Results.NotFound(new ApiError("No company account was found for this email."));
    }

    if (!string.IsNullOrWhiteSpace(request.ReviewerEmail) &&
        await store.CompanyReviewExistsAsync(company.Id, request.ReviewerEmail, request.WasteSubmissionId))
    {
        var duplicateMessage = request.WasteSubmissionId is null
            ? "You already submitted a review for this company."
            : "You already submitted a review for this order.";
        return Results.Conflict(new ApiError(duplicateMessage));
    }

    var review = new CompanyReview(
        Guid.NewGuid(),
        company.Id,
        request.WasteSubmissionId,
        request.ReviewerName.Trim(),
        EmptyToNull(request.ReviewerEmail),
        request.Rating,
        EmptyToNull(request.Comment),
        DateTimeOffset.UtcNow);

    await store.CreateCompanyReviewAsync(review);

    return Results.Created($"/api/company-reviews/{review.Id}", new CompanyReviewSummary(
        review.Id,
        review.ReviewerName,
        review.ReviewerEmail,
        review.Rating,
        review.Comment,
        review.CreatedAtUtc));
});

app.Run();

static WasteBidSummary BidToSummary(WasteBid bid) =>
    new(
        bid.Id,
        bid.WasteSubmissionId,
        bid.CompanyId,
        bid.CompanyName,
        bid.CompanyEmail,
        bid.Amount,
        bid.PickupDate,
        bid.Notes,
        bid.Status,
        bid.CreatedAtUtc);

static async Task<IResult> NotifyAndReturnCreatedBid(BidCreateResult.Created created, IEmailSender emailSender)
{
    if (!string.IsNullOrWhiteSpace(created.Submission.Email))
    {
        await emailSender.SendNotificationAsync(
            created.Submission.Email,
            "A recycler company placed a bid on your Green Cycle waste order",
            $"""
            {created.Bid.CompanyName} placed a bid on your waste order.

            Waste category: {created.Submission.WasteCategory}
            Pickup date: {created.Bid.PickupDate ?? "Not provided"}
            Notes: {created.Bid.Notes ?? "Not provided"}

            Log in to your Green Cycle dashboard to review and accept a bid.
            """);
    }

    return Results.Created(
        $"/api/waste/submissions/{created.Bid.WasteSubmissionId}/bids/{created.Bid.Id}",
        BidToSummary(created.Bid));
}

static async Task<IResult> NotifyAndReturnAcceptedBid(BidAcceptResult.Accepted accepted, IEmailSender emailSender)
{
    await emailSender.SendNotificationAsync(
        accepted.Bid.CompanyEmail,
        "Your Green Cycle bid was accepted",
        $"""
        Your bid was accepted by the waste submitter.

        Waste category: {accepted.Submission.WasteCategory}
        Pickup date: {accepted.Bid.PickupDate ?? "Not provided"}
        Submitter phone: {accepted.Submission.Phone}
        Submitter email: {accepted.Submission.Email ?? "Not provided"}
        Location: {accepted.Submission.Address ?? "Not provided"}

        Please contact the submitter to complete the pickup and sale.
        """);

    return Results.Ok(BidToSummary(accepted.Bid));
}

static async Task<IResult> NotifyAndReturnCompletedSubmission(CompleteSubmissionResult.Completed completed, IEmailSender emailSender)
{
    await emailSender.SendNotificationAsync(
        completed.Bid.CompanyEmail,
        "Green Cycle waste order completed",
        $"""
        The waste submitter marked this order as completed.

        Waste category: {completed.Submission.WasteCategory}
        Pickup date: {completed.Bid.PickupDate ?? "Not provided"}

        Thank you for completing this Green Cycle order.
        """);

    return Results.Ok(new CompletedSubmissionResponse(
        completed.Submission.Id,
        completed.Submission.Status,
        BidToSummary(completed.Bid)));
}

static string? GetRequired(IFormCollection form, string name)
{
    var value = form[name].ToString().Trim();
    return string.IsNullOrWhiteSpace(value) ? null : value;
}

static string? EmptyToNull(string? value)
{
    var trimmed = value?.Trim();
    return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
}

static string FindApiRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "GreenCycle.Api.csproj")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return Directory.GetCurrentDirectory();
}

static string FindSiteRoot(string apiRoot)
{
    var expectedSiteRoot = Path.GetFullPath(Path.Combine(apiRoot, "..", ".."));
    if (File.Exists(Path.Combine(expectedSiteRoot, "index.html")))
    {
        return expectedSiteRoot;
    }

    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "index.html")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return expectedSiteRoot;
}

static OtpDeliveryChannel NormalizeOtpChannel(string? channel)
{
    return string.Equals(channel, "whatsapp", StringComparison.OrdinalIgnoreCase)
        ? OtpDeliveryChannel.WhatsApp
        : OtpDeliveryChannel.Email;
}

static string MaskPhone(string phone)
{
    var digits = new string(phone.Where(char.IsDigit).ToArray());
    if (digits.Length <= 4)
    {
        return phone.Trim();
    }

    return new string('*', Math.Max(0, digits.Length - 4)) + digits[^4..];
}

static string[] SplitWasteCategories(string wasteCategory)
{
    return wasteCategory
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(category => !string.IsNullOrWhiteSpace(category))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static async Task<WasteSubmissionImage[]> SaveWasteImagesAsync(IReadOnlyList<IFormFile> imageFiles, string siteRoot, Guid submissionId)
{
    if (imageFiles.Count == 0)
    {
        return [];
    }

    var relativeFolder = Path.Combine("uploads", "waste", submissionId.ToString("N"));
    var outputFolder = Path.Combine(siteRoot, relativeFolder);
    Directory.CreateDirectory(outputFolder);

    var images = new List<WasteSubmissionImage>();
    for (var index = 0; index < imageFiles.Count; index++)
    {
        var file = imageFiles[index];
        if (file.Length <= 0)
        {
            continue;
        }

        var extension = NormalizeImageExtension(Path.GetExtension(file.FileName));
        var storedFileName = $"{index + 1}-{RandomNumberGenerator.GetHexString(8).ToLowerInvariant()}{extension}";
        var relativePath = Path.Combine(relativeFolder, storedFileName).Replace('\\', '/');
        var outputPath = Path.Combine(outputFolder, storedFileName);

        await using var stream = File.Create(outputPath);
        await file.CopyToAsync(stream);
        images.Add(new WasteSubmissionImage(relativePath, file.ContentType));
    }

    return images.ToArray();
}

static string NormalizeImageExtension(string? extension)
{
    var normalized = extension?.ToLowerInvariant();
    return normalized is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp"
        ? normalized
        : ".bin";
}

static WasteSubmissionImageSummary ToImageSummary(WasteSubmissionImage image)
{
    var normalizedPath = image.FileName.Replace('\\', '/').TrimStart('/');
    return new WasteSubmissionImageSummary(
        image.FileName,
        image.ContentType,
        normalizedPath.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase)
            ? $"/{normalizedPath}"
            : null);
}

public enum OtpDeliveryChannel
{
    Email,
    WhatsApp
}

public sealed record LoginRequest(string Email, string? Channel);

public sealed record OtpVerifyRequest(string Email, string Otp);

public sealed record WasteOtpRequest(string Phone, string? Email, string? Channel);

public sealed record WasteOtpVerifyRequest(string Phone, string Otp);

public sealed record WasteSubmission(
    Guid Id,
    string SubmitterType,
    string? SubmitterName,
    string? CompanyName,
    string Phone,
    string? Email,
    string? Address,
    string WasteCategory,
    string? Quantity,
    string? Notes,
    string? VerificationToken,
    string Status,
    DateTimeOffset CreatedAtUtc,
    WasteSubmissionImage[] Images,
    Guid[] TargetCompanyIds);

public sealed record WasteSubmissionImage(string FileName, string? ContentType);

public sealed record WasteSubmissionImageSummary(string FileName, string? ContentType, string? Url);

public sealed record CompanySignup(
    string CompanyName,
    string Mobile,
    string Email,
    string? Address,
    string? Website,
    string? LogoFileName,
    string[] WasteCategories);

public sealed record CompanyUser(
    Guid Id,
    string CompanyName,
    string Mobile,
    string Email,
    string? Address,
    string? Website,
    string? LogoFileName,
    string[] WasteCategories,
    DateTimeOffset CreatedAtUtc);

public sealed record AuthResponse(Guid Id, string CompanyName, string Email, string Message);

public sealed record OtpChallengeResponse(string Contact, string? MaskedPhone, string Message, string? DevOtp);

public sealed record AuthSessionResponse(Guid Id, string CompanyName, string Email, string Token, string Message);

public sealed record WasteOtpVerifiedResponse(string Phone, string VerificationToken, string Message);

public sealed record WasteSubmissionResponse(
    Guid Id,
    string SubmitterType,
    string Phone,
    string WasteCategory,
    string Status,
    string Message,
    AuthSessionResponse? Session = null);

public sealed record WastePrefillResponse(
    string SubmitterType,
    string? SubmitterName,
    string? CompanyName,
    string Phone,
    string? Email,
    string? Address,
    string[] WasteCategories,
    string? Notes);

public sealed record WasteSubmissionSummary(
    Guid Id,
    string SubmitterType,
    string? SubmitterName,
    string? CompanyName,
    string Phone,
    string? Email,
    string? Address,
    string WasteCategory,
    string? Notes,
    string Status,
    DateTimeOffset CreatedAtUtc,
    int ImageCount,
    WasteSubmissionImageSummary[] Images);

public sealed record WasteBid(
    Guid Id,
    Guid WasteSubmissionId,
    Guid CompanyId,
    string CompanyName,
    string CompanyEmail,
    decimal Amount,
    string? PickupDate,
    string? Notes,
    string Status,
    DateTimeOffset CreatedAtUtc);

public sealed record BidOpportunity(WasteSubmission Submission, WasteBid? ExistingBid);

public sealed record CompanyOrder(WasteSubmission Submission, WasteBid Bid);

public sealed record CompanyBid(WasteSubmission Submission, WasteBid Bid);

public sealed record BidOpportunitySummary(
    Guid Id,
    string SubmitterType,
    string? SubmitterName,
    string? CompanyName,
    string Phone,
    string? Email,
    string? Address,
    string WasteCategory,
    string? Notes,
    string Status,
    DateTimeOffset CreatedAtUtc,
    int ImageCount,
    WasteSubmissionImageSummary[] Images,
    Guid? ExistingBidId,
    decimal? ExistingBidAmount,
    string? ExistingBidPickupDate,
    string? ExistingBidStatus);

public sealed record CompanyOrderSummary(
    Guid Id,
    string SubmitterType,
    string? SubmitterName,
    string? CompanyName,
    string Phone,
    string? Email,
    string? Address,
    string WasteCategory,
    string? Notes,
    string Status,
    DateTimeOffset CreatedAtUtc,
    int ImageCount,
    WasteSubmissionImageSummary[] Images,
    WasteBidSummary AcceptedBid);

public sealed record CompanyBidSummary(
    Guid Id,
    string SubmitterType,
    string? SubmitterName,
    string? CompanyName,
    string Phone,
    string? Email,
    string? Address,
    string WasteCategory,
    string? Notes,
    string Status,
    DateTimeOffset CreatedAtUtc,
    int ImageCount,
    WasteSubmissionImageSummary[] Images,
    WasteBidSummary Bid);

public sealed record BidCreateRequest(string? CompanyEmail, string? PickupDate, string? Notes);

public sealed record BidAcceptRequest(string? RequesterEmail);

public sealed record CompleteSubmissionRequest(string? RequesterEmail);

public sealed record WasteBidSummary(
    Guid Id,
    Guid WasteSubmissionId,
    Guid CompanyId,
    string CompanyName,
    string CompanyEmail,
    decimal Amount,
    string? PickupDate,
    string? Notes,
    string Status,
    DateTimeOffset CreatedAtUtc);

public sealed record CompletedSubmissionResponse(Guid Id, string Status, WasteBidSummary AcceptedBid);

public sealed record CompanyReview(
    Guid Id,
    Guid CompanyId,
    Guid? WasteSubmissionId,
    string ReviewerName,
    string? ReviewerEmail,
    int Rating,
    string? Comment,
    DateTimeOffset CreatedAtUtc);

public sealed record CompanyReviewRequest(
    string? CompanyEmail,
    Guid? WasteSubmissionId,
    string? ReviewerName,
    string? ReviewerEmail,
    int Rating,
    string? Comment);

public sealed record CompanyReviewSummary(
    Guid Id,
    string ReviewerName,
    string? ReviewerEmail,
    int Rating,
    string? Comment,
    DateTimeOffset CreatedAtUtc);

public sealed record CompanyReviewsResponse(
    double AverageRating,
    int ReviewCount,
    IEnumerable<CompanyReviewSummary> Reviews);

public sealed record ApiError(string Message);

public sealed record EmailSendResult(bool Success, string? ErrorMessage = null);

public sealed record WhatsAppSendResult(bool Success, string? ErrorMessage = null);

public sealed class SmtpOptions
{
    public string? Host { get; init; }
    public int Port { get; init; } = 587;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? FromEmail { get; init; }
    public string? FromName { get; init; } = "Green Cycle";
    public bool EnableSsl { get; init; } = true;
}

public interface IEmailSender
{
    Task<EmailSendResult> SendOtpAsync(string toEmail, string otp);
    Task<EmailSendResult> SendNotificationAsync(string toEmail, string subject, string body);
}

public interface IWhatsAppSender
{
    Task<WhatsAppSendResult> SendOtpAsync(string toPhone, string otp);
}

public sealed class WhatsAppOptions
{
    public string? AccessToken { get; init; }
    public string? PhoneNumberId { get; init; }
    public string ApiVersion { get; init; } = "v20.0";
    public string? TemplateName { get; init; }
    public string LanguageCode { get; init; } = "en_US";
}

public sealed class WhatsAppCloudSender(HttpClient httpClient, IOptions<WhatsAppOptions> options) : IWhatsAppSender
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly WhatsAppOptions options = options.Value;

    public async Task<WhatsAppSendResult> SendOtpAsync(string toPhone, string otp)
    {
        if (string.IsNullOrWhiteSpace(options.AccessToken) ||
            string.IsNullOrWhiteSpace(options.PhoneNumberId))
        {
            return new WhatsAppSendResult(false, "WhatsApp OTP is not configured on the server.");
        }

        var normalizedPhone = NormalizeWhatsAppPhone(toPhone);
        if (string.IsNullOrWhiteSpace(normalizedPhone))
        {
            return new WhatsAppSendResult(false, "Please enter a WhatsApp phone number with country code.");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://graph.facebook.com/{options.ApiVersion.Trim('/')}/{options.PhoneNumberId}/messages");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.AccessToken);
        request.Content = new StringContent(BuildMessagePayload(normalizedPhone, otp), Encoding.UTF8, "application/json");

        try
        {
            using var response = await httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return new WhatsAppSendResult(true);
            }

            return new WhatsAppSendResult(false, "Could not send WhatsApp OTP. Check WhatsApp Cloud API configuration.");
        }
        catch
        {
            return new WhatsAppSendResult(false, "Could not send WhatsApp OTP. Please try again later.");
        }
    }

    private string BuildMessagePayload(string toPhone, string otp)
    {
        if (!string.IsNullOrWhiteSpace(options.TemplateName))
        {
            var templatePayload = new
            {
                messaging_product = "whatsapp",
                to = toPhone,
                type = "template",
                template = new
                {
                    name = options.TemplateName,
                    language = new { code = options.LanguageCode },
                    components = new object[]
                    {
                        new
                        {
                            type = "body",
                            parameters = new object[]
                            {
                                new { type = "text", text = otp }
                            }
                        }
                    }
                }
            };

            return JsonSerializer.Serialize(templatePayload, SerializerOptions);
        }

        var textPayload = new
        {
            messaging_product = "whatsapp",
            to = toPhone,
            type = "text",
            text = new
            {
                preview_url = false,
                body = $"Your Green Cycle verification code is {otp}. It expires in 5 minutes."
            }
        };

        return JsonSerializer.Serialize(textPayload, SerializerOptions);
    }

    private static string NormalizeWhatsAppPhone(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.StartsWith("00", StringComparison.Ordinal) ? digits[2..] : digits;
    }
}

public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    private readonly SmtpOptions options = options.Value;

    public async Task<EmailSendResult> SendOtpAsync(string toEmail, string otp)
    {
        if (string.IsNullOrWhiteSpace(options.Host) ||
            string.IsNullOrWhiteSpace(options.FromEmail) ||
            string.IsNullOrWhiteSpace(options.Username) ||
            string.IsNullOrWhiteSpace(options.Password))
        {
            return new EmailSendResult(false, "Email OTP is not configured on the server.");
        }

        using var message = new MailMessage
        {
            From = new MailAddress(options.FromEmail, options.FromName),
            Subject = "Your Green Cycle verification code"
        };
        message.To.Add(toEmail);

        var plainTextBody = $"""
            Green Cycle verification

            Your verification code is: {otp}

            This code expires in 5 minutes. If you did not request this code, you can ignore this email.

            Green Cycle Platform
            Circular waste solutions platform
            """;

        var logoPath = FindLogoPath();
        var logoContentId = "greencycle-logo";
        var htmlBody = BuildOtpEmailHtml(otp, logoPath is not null ? logoContentId : null);

        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
            plainTextBody,
            null,
            MediaTypeNames.Text.Plain));

        var htmlView = AlternateView.CreateAlternateViewFromString(
            htmlBody,
            null,
            MediaTypeNames.Text.Html);

        if (logoPath is not null)
        {
            var logo = new LinkedResource(logoPath, new ContentType(MediaTypeNames.Image.Png))
            {
                ContentId = logoContentId,
                TransferEncoding = TransferEncoding.Base64
            };
            htmlView.LinkedResources.Add(logo);
        }

        message.AlternateViews.Add(htmlView);

        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.EnableSsl,
            Credentials = new NetworkCredential(options.Username, options.Password)
        };

        try
        {
            await client.SendMailAsync(message);
            return new EmailSendResult(true);
        }
        catch
        {
            return new EmailSendResult(false, "Could not send OTP email. Please try again later.");
        }
    }

    public async Task<EmailSendResult> SendNotificationAsync(string toEmail, string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(options.Host) ||
            string.IsNullOrWhiteSpace(options.FromEmail) ||
            string.IsNullOrWhiteSpace(options.Username) ||
            string.IsNullOrWhiteSpace(options.Password))
        {
            return new EmailSendResult(false, "Email notifications are not configured on the server.");
        }

        using var message = new MailMessage
        {
            From = new MailAddress(options.FromEmail, options.FromName),
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };
        message.To.Add(toEmail);

        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.EnableSsl,
            Credentials = new NetworkCredential(options.Username, options.Password)
        };

        try
        {
            await client.SendMailAsync(message);
            return new EmailSendResult(true);
        }
        catch
        {
            return new EmailSendResult(false, "Could not send notification email. Please try again later.");
        }
    }

    private static string BuildOtpEmailHtml(string otp, string? logoContentId)
    {
        var encodedOtp = WebUtility.HtmlEncode(otp);
        var logoHtml = logoContentId is null
            ? ""
            : $"""
              <div style="margin-top:28px;padding-top:18px;border-top:1px solid #dce8d8;text-align:center;">
                <img src="cid:{logoContentId}" alt="Green Cycle" style="width:92px;max-width:40%;height:auto;margin-bottom:10px;">
              </div>
              """;

        return $$"""
            <!doctype html>
            <html>
            <body style="margin:0;background:#f5f8f2;font-family:Arial,Helvetica,sans-serif;color:#173d25;">
              <div style="max-width:620px;margin:0 auto;padding:28px 18px;">
                <div style="background:#ffffff;border:1px solid #dce8d8;border-radius:12px;padding:28px;box-shadow:0 12px 34px rgba(23,69,42,0.08);">
                  <p style="margin:0 0 8px;color:#5b7566;font-size:14px;">Green Cycle Platform</p>
                  <h1 style="margin:0 0 14px;font-size:24px;line-height:1.25;color:#17452a;">Your verification code</h1>
                  <p style="margin:0 0 18px;font-size:16px;line-height:1.6;color:#405f4a;">
                    Use this one-time code to continue your Green Cycle login or waste submission.
                  </p>
                  <div style="letter-spacing:6px;font-size:32px;font-weight:800;text-align:center;background:#e6f6e8;color:#1d6f32;border:1px solid #bbf7d0;border-radius:10px;padding:18px;margin:22px 0;">
                    {{encodedOtp}}
                  </div>
                  <p style="margin:0 0 10px;font-size:14px;line-height:1.6;color:#5b7566;">
                    This code expires in 5 minutes. If you did not request it, you can safely ignore this email.
                  </p>
                  <p style="margin:0;font-size:14px;line-height:1.6;color:#5b7566;">
                    Thank you for helping route waste into safer reuse and recycling channels.
                  </p>
                  {{logoHtml}}
                </div>
              </div>
            </body>
            </html>
            """;
    }

    private static string? FindLogoPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var logoPath = Path.Combine(directory.FullName, "Green_logo-removebg-preview.png");
            if (File.Exists(logoPath))
            {
                return logoPath;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

public sealed record CompanySummary(
    Guid Id,
    string CompanyName,
    string Mobile,
    string Email,
    string? Address,
    string? Website,
    string[] WasteCategories,
    DateTimeOffset CreatedAtUtc);

public abstract record SignupResult
{
    public sealed record Created(CompanyUser User) : SignupResult;
    public sealed record Duplicate : SignupResult;
}

public abstract record BidCreateResult
{
    public sealed record Created(WasteBid Bid, WasteSubmission Submission) : BidCreateResult;
    public sealed record Duplicate : BidCreateResult;
    public sealed record NotFound : BidCreateResult;
    public sealed record OwnSubmission : BidCreateResult;
}

public abstract record BidAcceptResult
{
    public sealed record Accepted(WasteBid Bid, WasteSubmission Submission) : BidAcceptResult;
    public sealed record NotFound : BidAcceptResult;
    public sealed record NotOwner : BidAcceptResult;
}

public abstract record CompleteSubmissionResult
{
    public sealed record Completed(WasteBid Bid, WasteSubmission Submission) : CompleteSubmissionResult;
    public sealed record NotFound : CompleteSubmissionResult;
    public sealed record NotOwner : CompleteSubmissionResult;
    public sealed record NoAcceptedBid : CompleteSubmissionResult;
}

public sealed record OtpChallenge(string Email, string Code, DateTimeOffset ExpiresAtUtc);

public sealed class OtpStore
{
    private readonly Dictionary<string, OtpChallenge> challenges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> verifiedTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    public OtpChallenge Create(string email)
    {
        var normalizedEmail = email.Trim();
        var challenge = new OtpChallenge(
            normalizedEmail,
            RandomNumberGenerator.GetInt32(100000, 1000000).ToString(),
            DateTimeOffset.UtcNow.AddMinutes(5));

        lock (gate)
        {
            challenges[normalizedEmail] = challenge;
        }

        return challenge;
    }

    public bool Verify(string email, string code)
    {
        var normalizedEmail = email.Trim();

        lock (gate)
        {
            if (!challenges.TryGetValue(normalizedEmail, out var challenge))
            {
                return false;
            }

            if (challenge.ExpiresAtUtc < DateTimeOffset.UtcNow)
            {
                challenges.Remove(normalizedEmail);
                return false;
            }

            if (challenge.Code != code.Trim())
            {
                return false;
            }

            challenges.Remove(normalizedEmail);
            return true;
        }
    }

    public string CreateVerifiedToken(string contact)
    {
        var normalizedContact = contact.Trim();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

        lock (gate)
        {
            verifiedTokens[normalizedContact] = token;
        }

        return token;
    }

    public bool ConsumeVerifiedToken(string contact, string token)
    {
        var normalizedContact = contact.Trim();

        lock (gate)
        {
            if (!verifiedTokens.TryGetValue(normalizedContact, out var storedToken))
            {
                return false;
            }

            if (storedToken != token.Trim())
            {
                return false;
            }

            verifiedTokens.Remove(normalizedContact);
            return true;
        }
    }

}

public sealed class UserStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string legacyFilePath;
    private readonly string connectionString;

    public UserStore(string apiRoot)
    {
        var dataDirectory = Path.Combine(apiRoot, "App_Data");
        Directory.CreateDirectory(dataDirectory);

        legacyFilePath = Path.Combine(dataDirectory, "users.json");
        var databasePath = Path.Combine(dataDirectory, "greencycle.db");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync()
    {
        await gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            await CreateSchemaAsync(connection);
            await MigrateLegacyUsersAsync(connection);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SignupResult> CreateAsync(CompanySignup signup)
    {
        await gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            if (await EmailExistsAsync(connection, signup.Email.Trim()))
            {
                return new SignupResult.Duplicate();
            }

            var user = new CompanyUser(
                Guid.NewGuid(),
                signup.CompanyName.Trim(),
                signup.Mobile.Trim(),
                signup.Email.Trim(),
                EmptyToNull(signup.Address),
                EmptyToNull(signup.Website),
                EmptyToNull(signup.LogoFileName),
                signup.WasteCategories,
                DateTimeOffset.UtcNow);

            await InsertCompanyAsync(connection, user);
            await transaction.CommitAsync();
            return new SignupResult.Created(user);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CompanyUser?> FindByEmailAsync(string email)
    {
        await gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            return await FindByEmailAsync(connection, email.Trim());
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<CompanyUser>> GetAllAsync()
    {
        await gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            return await ReadUsersAsync(connection);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CompanyUser?> CreateWasteSubmissionAsync(WasteSubmission submission)
    {
        await gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO waste_submissions (
                    id,
                    submitter_type,
                    submitter_name,
                    company_name,
                    phone,
                    email,
                    address,
                    waste_category,
                    quantity,
                    notes,
                    verification_token,
                    status,
                    created_at_utc
                )
                VALUES (
                    $id,
                    $submitterType,
                    $submitterName,
                    $companyName,
                    $phone,
                    $email,
                    $address,
                    $wasteCategory,
                    $quantity,
                    $notes,
                    $verificationToken,
                    $status,
                    $createdAtUtc
                );
                """;

            command.Parameters.AddWithValue("$id", submission.Id.ToString());
            command.Parameters.AddWithValue("$submitterType", submission.SubmitterType);
            command.Parameters.AddWithValue("$submitterName", ToDbValue(submission.SubmitterName));
            command.Parameters.AddWithValue("$companyName", ToDbValue(submission.CompanyName));
            command.Parameters.AddWithValue("$phone", submission.Phone);
            command.Parameters.AddWithValue("$email", ToDbValue(submission.Email));
            command.Parameters.AddWithValue("$address", ToDbValue(submission.Address));
            command.Parameters.AddWithValue("$wasteCategory", submission.WasteCategory);
            command.Parameters.AddWithValue("$quantity", ToDbValue(submission.Quantity));
            command.Parameters.AddWithValue("$notes", ToDbValue(submission.Notes));
            command.Parameters.AddWithValue("$verificationToken", ToDbValue(submission.VerificationToken));
            command.Parameters.AddWithValue("$status", submission.Status);
            command.Parameters.AddWithValue("$createdAtUtc", submission.CreatedAtUtc.ToString("O"));
            await command.ExecuteNonQueryAsync();

            foreach (var image in submission.Images)
            {
                var imageCommand = connection.CreateCommand();
                imageCommand.CommandText = """
                    INSERT INTO waste_submission_images (
                        waste_submission_id,
                        file_name,
                        content_type,
                        created_at_utc
                    )
                    VALUES (
                        $wasteSubmissionId,
                        $fileName,
                        $contentType,
                        $createdAtUtc
                    );
                    """;
                imageCommand.Parameters.AddWithValue("$wasteSubmissionId", submission.Id.ToString());
                imageCommand.Parameters.AddWithValue("$fileName", image.FileName);
                imageCommand.Parameters.AddWithValue("$contentType", ToDbValue(image.ContentType));
                imageCommand.Parameters.AddWithValue("$createdAtUtc", submission.CreatedAtUtc.ToString("O"));
                await imageCommand.ExecuteNonQueryAsync();
            }

            foreach (var targetCompanyId in submission.TargetCompanyIds)
            {
                var targetCommand = connection.CreateCommand();
                targetCommand.CommandText = """
                    INSERT OR IGNORE INTO waste_submission_targets (
                        waste_submission_id,
                        company_id
                    )
                    VALUES (
                        $wasteSubmissionId,
                        $companyId
                    );
                    """;
                targetCommand.Parameters.AddWithValue("$wasteSubmissionId", submission.Id.ToString());
                targetCommand.Parameters.AddWithValue("$companyId", targetCompanyId.ToString());
                await targetCommand.ExecuteNonQueryAsync();
            }

            var sessionUser = await EnsureWasteSubmitterAccountAsync(connection, submission);
            await transaction.CommitAsync();
            return sessionUser;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<CompanyUser?> EnsureWasteSubmitterAccountAsync(SqliteConnection connection, WasteSubmission submission)
    {
        if (string.IsNullOrWhiteSpace(submission.Email))
        {
            return null;
        }

        var existing = await FindByEmailAsync(connection, submission.Email.Trim());
        if (existing is not null)
        {
            return existing;
        }

        var displayName = submission.CompanyName ??
            submission.SubmitterName ??
            $"Waste submitter {submission.Phone}";
        var categories = SplitWasteCategories(submission.WasteCategory);
        if (categories.Length == 0)
        {
            categories = ["General waste"];
        }

        var user = new CompanyUser(
            Guid.NewGuid(),
            displayName.Trim(),
            submission.Phone.Trim(),
            submission.Email.Trim(),
            EmptyToNull(submission.Address),
            null,
            null,
            categories,
            DateTimeOffset.UtcNow);

        await InsertCompanyAsync(connection, user);
        return user;
    }

    private static string[] SplitWasteCategories(string wasteCategory)
    {
        return wasteCategory
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizePhoneDigits(string phone)
    {
        return new string(phone.Where(char.IsDigit).ToArray());
    }

    public async Task<IReadOnlyList<WasteSubmission>> GetWasteSubmissionsByEmailAsync(string email)
    {
        await gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    id,
                    submitter_type,
                    submitter_name,
                    company_name,
                    phone,
                    email,
                    address,
                    waste_category,
                    quantity,
                    notes,
                    verification_token,
                    status,
                    created_at_utc
                FROM waste_submissions
                WHERE email = $email
                ORDER BY created_at_utc DESC;
                """;
            command.Parameters.AddWithValue("$email", email.Trim());

            var submissions = new List<WasteSubmission>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = Guid.Parse(reader.GetString(0));
                submissions.Add(new WasteSubmission(
                    id,
                    reader.GetString(1),
                    GetNullableString(reader, 2),
                    GetNullableString(reader, 3),
                    reader.GetString(4),
                    GetNullableString(reader, 5),
                    GetNullableString(reader, 6),
                    reader.GetString(7),
                    GetNullableString(reader, 8),
                    GetNullableString(reader, 9),
                    GetNullableString(reader, 10),
                    reader.GetString(11),
                    DateTimeOffset.Parse(reader.GetString(12)),
                    [],
                    []));
            }

            return await AddImagesToSubmissionsAsync(connection, submissions);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WasteSubmission?> GetLatestWasteSubmissionByPhoneAsync(string phone)
    {
        await gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            var phoneDigits = NormalizePhoneDigits(phone);
            var phoneWithoutCountryCode = phoneDigits.StartsWith("965", StringComparison.Ordinal) && phoneDigits.Length > 8
                ? phoneDigits[3..]
                : phoneDigits;
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    id,
                    submitter_type,
                    submitter_name,
                    company_name,
                    phone,
                    email,
                    address,
                    waste_category,
                    quantity,
                    notes,
                    verification_token,
                    status,
                    created_at_utc,
                    (
                        SELECT COUNT(*)
                        FROM waste_submission_images
                        WHERE waste_submission_id = waste_submissions.id
                    ) AS image_count
                FROM waste_submissions
                ORDER BY created_at_utc DESC
                """;

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var storedPhoneDigits = NormalizePhoneDigits(reader.GetString(4));
                var storedPhoneWithoutCountryCode = storedPhoneDigits.StartsWith("965", StringComparison.Ordinal) && storedPhoneDigits.Length > 8
                    ? storedPhoneDigits[3..]
                    : storedPhoneDigits;

                if (string.Equals(reader.GetString(4), phone.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(phoneDigits) && storedPhoneDigits == phoneDigits) ||
                    (!string.IsNullOrWhiteSpace(phoneWithoutCountryCode) && storedPhoneWithoutCountryCode == phoneWithoutCountryCode))
                {
                    return new WasteSubmission(
                        Guid.Parse(reader.GetString(0)),
                        reader.GetString(1),
                        GetNullableString(reader, 2),
                        GetNullableString(reader, 3),
                        reader.GetString(4),
                        GetNullableString(reader, 5),
                        GetNullableString(reader, 6),
                        reader.GetString(7),
                        GetNullableString(reader, 8),
                        GetNullableString(reader, 9),
                        GetNullableString(reader, 10),
                        reader.GetString(11),
                        DateTimeOffset.Parse(reader.GetString(12)),
                        new WasteSubmissionImage[reader.GetInt32(13)],
                        []);
                }
            }

            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<CompanyUser>> GetMatchingCompaniesForSubmissionAsync(WasteSubmission submission)
    {
        await gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            var companies = await ReadUsersAsync(connection);
            var targetIds = submission.TargetCompanyIds.ToHashSet();

            return companies
                .Where(company => !string.Equals(company.Email, submission.Email, StringComparison.OrdinalIgnoreCase))
                .Where(company => targetIds.Count == 0 || targetIds.Contains(company.Id))
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<BidOpportunity>> GetBidOpportunitiesAsync(string companyEmail)
    {
        await gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            var company = await FindByEmailAsync(connection, companyEmail.Trim());
            if (company is null)
            {
                return [];
            }

            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    id,
                    submitter_type,
                    submitter_name,
                    company_name,
                    phone,
                    email,
                    address,
                    waste_category,
                    quantity,
                    notes,
                    verification_token,
                    status,
                    created_at_utc
                FROM waste_submissions
                WHERE status IN ('received', 'bidding')
                  AND (email IS NULL OR email <> $companyEmail COLLATE NOCASE)
                  AND (
                    NOT EXISTS (
                        SELECT 1
                        FROM waste_submission_targets
                        WHERE waste_submission_id = waste_submissions.id
                    )
                    OR EXISTS (
                        SELECT 1
                        FROM waste_submission_targets
                        WHERE waste_submission_id = waste_submissions.id
                          AND company_id = $companyId
                    )
                  )
                ORDER BY created_at_utc DESC;
                """;
            command.Parameters.AddWithValue("$companyEmail", company.Email);
            command.Parameters.AddWithValue("$companyId", company.Id.ToString());

            var submissions = new List<WasteSubmission>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                submissions.Add(ReadWasteSubmission(reader));
            }

            submissions = [.. await AddImagesToSubmissionsAsync(connection, submissions)];

            var opportunities = new List<BidOpportunity>();
            foreach (var submission in submissions)
            {
                var existingBid = await ReadBidBySubmissionAndCompanyAsync(connection, submission.Id, company.Id);
                opportunities.Add(new BidOpportunity(submission, existingBid));
            }

            return opportunities;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<CompanyOrder>> GetCompanyOrdersAsync(string companyEmail)
    {
        await gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            var company = await FindByEmailAsync(connection, companyEmail.Trim());
            if (company is null)
            {
                return [];
            }

            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    ws.id,
                    ws.submitter_type,
                    ws.submitter_name,
                    ws.company_name,
                    ws.phone,
                    ws.email,
                    ws.address,
                    ws.waste_category,
                    ws.quantity,
                    ws.notes,
                    ws.verification_token,
                    ws.status,
                    ws.created_at_utc,
                    wb.id,
                    wb.waste_submission_id,
                    wb.company_id,
                    c.company_name,
                    c.email,
                    wb.amount,
                    wb.pickup_date,
                    wb.notes,
                    wb.status,
                    wb.created_at_utc
                FROM waste_bids wb
                INNER JOIN waste_submissions ws ON ws.id = wb.waste_submission_id
                INNER JOIN companies c ON c.id = wb.company_id
                WHERE wb.company_id = $companyId
                  AND wb.status = 'accepted'
                  AND ws.status IN ('pickup_pending', 'accepted', 'completed')
                ORDER BY ws.created_at_utc DESC;
                """;
            command.Parameters.AddWithValue("$companyId", company.Id.ToString());

            var orders = new List<CompanyOrder>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var submission = ReadWasteSubmission(reader);
                var bid = new WasteBid(
                    Guid.Parse(reader.GetString(13)),
                    Guid.Parse(reader.GetString(14)),
                    Guid.Parse(reader.GetString(15)),
                    reader.GetString(16),
                    reader.GetString(17),
                    reader.GetDecimal(18),
                    GetNullableString(reader, 19),
                    GetNullableString(reader, 20),
                    reader.GetString(21),
                    DateTimeOffset.Parse(reader.GetString(22)));
                orders.Add(new CompanyOrder(submission, bid));
            }

            var submissionsWithImages = await AddImagesToSubmissionsAsync(
                connection,
                orders.Select(order => order.Submission).ToArray());
            var submissionsById = submissionsWithImages.ToDictionary(submission => submission.Id);

            return orders
                .Select(order => order with { Submission = submissionsById[order.Submission.Id] })
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<CompanyBid>> GetCompanyBidsAsync(string companyEmail)
    {
        await gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            var company = await FindByEmailAsync(connection, companyEmail.Trim());
            if (company is null)
            {
                return [];
            }

            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    ws.id,
                    ws.submitter_type,
                    ws.submitter_name,
                    ws.company_name,
                    ws.phone,
                    ws.email,
                    ws.address,
                    ws.waste_category,
                    ws.quantity,
                    ws.notes,
                    ws.verification_token,
                    ws.status,
                    ws.created_at_utc,
                    wb.id,
                    wb.waste_submission_id,
                    wb.company_id,
                    c.company_name,
                    c.email,
                    wb.amount,
                    wb.pickup_date,
                    wb.notes,
                    wb.status,
                    wb.created_at_utc
                FROM waste_bids wb
                INNER JOIN waste_submissions ws ON ws.id = wb.waste_submission_id
                INNER JOIN companies c ON c.id = wb.company_id
                WHERE wb.company_id = $companyId
                ORDER BY wb.created_at_utc DESC;
                """;
            command.Parameters.AddWithValue("$companyId", company.Id.ToString());

            var bids = new List<CompanyBid>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var submission = ReadWasteSubmission(reader);
                var bid = new WasteBid(
                    Guid.Parse(reader.GetString(13)),
                    Guid.Parse(reader.GetString(14)),
                    Guid.Parse(reader.GetString(15)),
                    reader.GetString(16),
                    reader.GetString(17),
                    reader.GetDecimal(18),
                    GetNullableString(reader, 19),
                    GetNullableString(reader, 20),
                    reader.GetString(21),
                    DateTimeOffset.Parse(reader.GetString(22)));
                bids.Add(new CompanyBid(submission, bid));
            }

            var submissionsWithImages = await AddImagesToSubmissionsAsync(
                connection,
                bids.Select(item => item.Submission).ToArray());
            var submissionsById = submissionsWithImages.ToDictionary(submission => submission.Id);

            return bids
                .Select(item => item with { Submission = submissionsById[item.Submission.Id] })
                .ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<BidCreateResult> CreateWasteBidAsync(Guid submissionId, string companyEmail, DateOnly pickupDate, string? notes)
    {
        await gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var company = await FindByEmailAsync(connection, companyEmail.Trim());
            var submission = await ReadWasteSubmissionByIdAsync(connection, submissionId);

            if (company is null || submission is null || submission.Status is "pickup_pending" or "completed")
            {
                return new BidCreateResult.NotFound();
            }

            if (string.Equals(submission.Email, company.Email, StringComparison.OrdinalIgnoreCase))
            {
                return new BidCreateResult.OwnSubmission();
            }

            if (await ReadBidBySubmissionAndCompanyAsync(connection, submissionId, company.Id) is not null)
            {
                return new BidCreateResult.Duplicate();
            }

            var bid = new WasteBid(
                Guid.NewGuid(),
                submissionId,
                company.Id,
                company.CompanyName,
                company.Email,
                0,
                pickupDate.ToString("yyyy-MM-dd"),
                EmptyToNull(notes),
                "pending",
                DateTimeOffset.UtcNow);

            await InsertBidAsync(connection, bid);
            await UpdateSubmissionStatusAsync(connection, submissionId, "bidding");
            await transaction.CommitAsync();
            return new BidCreateResult.Created(bid, submission);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<WasteBid>?> GetBidsForSubmissionAsync(Guid submissionId, string requesterEmail)
    {
        await gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            var submission = await ReadWasteSubmissionByIdAsync(connection, submissionId);
            if (submission is null ||
                !string.Equals(submission.Email, requesterEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return await ReadBidsForSubmissionAsync(connection, submissionId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<BidAcceptResult> AcceptWasteBidAsync(Guid submissionId, Guid bidId, string requesterEmail)
    {
        await gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var submission = await ReadWasteSubmissionByIdAsync(connection, submissionId);
            if (submission is null)
            {
                return new BidAcceptResult.NotFound();
            }

            if (!string.Equals(submission.Email, requesterEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return new BidAcceptResult.NotOwner();
            }

            var bid = await ReadBidByIdAsync(connection, bidId, submissionId);
            if (bid is null)
            {
                return new BidAcceptResult.NotFound();
            }

            await RejectOtherBidsAsync(connection, submissionId, bidId);
            await UpdateBidStatusAsync(connection, bidId, "accepted");
            await UpdateSubmissionStatusAsync(connection, submissionId, "pickup_pending");
            await transaction.CommitAsync();
            return new BidAcceptResult.Accepted(bid with { Status = "accepted" }, submission with { Status = "pickup_pending" });
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<CompleteSubmissionResult> CompleteWasteSubmissionAsync(Guid submissionId, string requesterEmail)
    {
        await gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var submission = await ReadWasteSubmissionByIdAsync(connection, submissionId);
            if (submission is null)
            {
                return new CompleteSubmissionResult.NotFound();
            }

            if (!string.Equals(submission.Email, requesterEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return new CompleteSubmissionResult.NotOwner();
            }

            var acceptedBid = await ReadAcceptedBidForSubmissionAsync(connection, submissionId);
            if (acceptedBid is null)
            {
                return new CompleteSubmissionResult.NoAcceptedBid();
            }

            await UpdateSubmissionStatusAsync(connection, submissionId, "completed");
            await transaction.CommitAsync();
            return new CompleteSubmissionResult.Completed(acceptedBid, submission with { Status = "completed" });
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CreateCompanyReviewAsync(CompanyReview review)
    {
        await gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO company_reviews (
                    id,
                    company_id,
                    waste_submission_id,
                    reviewer_name,
                    reviewer_email,
                    rating,
                    comment,
                    created_at_utc
                )
                VALUES (
                    $id,
                    $companyId,
                    $wasteSubmissionId,
                    $reviewerName,
                    $reviewerEmail,
                    $rating,
                    $comment,
                    $createdAtUtc
                );
                """;
            command.Parameters.AddWithValue("$id", review.Id.ToString());
            command.Parameters.AddWithValue("$companyId", review.CompanyId.ToString());
            command.Parameters.AddWithValue("$wasteSubmissionId", ToDbValue(review.WasteSubmissionId?.ToString()));
            command.Parameters.AddWithValue("$reviewerName", review.ReviewerName);
            command.Parameters.AddWithValue("$reviewerEmail", ToDbValue(review.ReviewerEmail));
            command.Parameters.AddWithValue("$rating", review.Rating);
            command.Parameters.AddWithValue("$comment", ToDbValue(review.Comment));
            command.Parameters.AddWithValue("$createdAtUtc", review.CreatedAtUtc.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> CompanyReviewExistsAsync(Guid companyId, string reviewerEmail, Guid? wasteSubmissionId)
    {
        await gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            var command = connection.CreateCommand();
            command.CommandText = wasteSubmissionId is null
                ? """
                  SELECT 1
                  FROM company_reviews
                  WHERE company_id = $companyId
                    AND reviewer_email = $reviewerEmail COLLATE NOCASE
                    AND waste_submission_id IS NULL
                  LIMIT 1;
                  """
                : """
                  SELECT 1
                  FROM company_reviews
                  WHERE company_id = $companyId
                    AND reviewer_email = $reviewerEmail COLLATE NOCASE
                    AND waste_submission_id = $wasteSubmissionId
                  LIMIT 1;
                  """;
            command.Parameters.AddWithValue("$companyId", companyId.ToString());
            command.Parameters.AddWithValue("$reviewerEmail", reviewerEmail.Trim());
            if (wasteSubmissionId is not null)
            {
                command.Parameters.AddWithValue("$wasteSubmissionId", wasteSubmissionId.Value.ToString());
            }
            return await command.ExecuteScalarAsync() is not null;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<CompanyReview>> GetCompanyReviewsByEmailAsync(string companyEmail)
    {
        await gate.WaitAsync();
        try
        {
            await using var connection = await OpenConnectionAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    cr.id,
                    cr.company_id,
                    cr.waste_submission_id,
                    cr.reviewer_name,
                    cr.reviewer_email,
                    cr.rating,
                    cr.comment,
                    cr.created_at_utc
                FROM company_reviews cr
                INNER JOIN companies c ON c.id = cr.company_id
                WHERE c.email = $companyEmail
                ORDER BY cr.created_at_utc DESC;
                """;
            command.Parameters.AddWithValue("$companyEmail", companyEmail.Trim());

            var reviews = new List<CompanyReview>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                reviews.Add(new CompanyReview(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    GetNullableGuid(reader, 2),
                    reader.GetString(3),
                    GetNullableString(reader, 4),
                    reader.GetInt32(5),
                    GetNullableString(reader, 6),
                    DateTimeOffset.Parse(reader.GetString(7))));
            }

            return reviews;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool CategoriesOverlap(IEnumerable<string> companyCategories, IEnumerable<string> submissionCategories)
    {
        var companyKeys = companyCategories.SelectMany(GetCategoryKeys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var submissionKeys = submissionCategories.SelectMany(GetCategoryKeys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (companyKeys.Length > 0 && submissionKeys.Length > 0)
        {
            return companyKeys.Intersect(submissionKeys, StringComparer.OrdinalIgnoreCase).Any();
        }

        var submissionList = submissionCategories
            .Select(category => category.Trim())
            .Where(category => category.Length > 0)
            .ToArray();

        return companyCategories.Any(companyCategory =>
            submissionList.Any(submissionCategory =>
                companyCategory.Contains(submissionCategory, StringComparison.OrdinalIgnoreCase) ||
                submissionCategory.Contains(companyCategory, StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<string> GetCategoryKeys(string category)
    {
        var normalized = category.Trim().ToLowerInvariant();
        foreach (var (key, tokens) in CategoryTokens)
        {
            if (tokens.Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                yield return key;
            }
        }
    }

    private static readonly IReadOnlyDictionary<string, string[]> CategoryTokens = new Dictionary<string, string[]>
    {
        ["plastic"] = ["plastic", "بلاستيك"],
        ["paper"] = ["paper", "cardboard", "ورق", "كرتون"],
        ["metal"] = ["metal", "scrap", "aluminum", "خردة", "معدن", "المعدنية"],
        ["glass"] = ["glass", "زجاج"],
        ["electronics"] = ["electronic", "electrical", "mobile", "cable", "wire", "إلكترون", "كهرب", "هواتف", "كيبل", "واير"],
        ["organic"] = ["organic", "زراعية", "عضوية"],
        ["textile"] = ["textile", "نسيج"],
        ["construction"] = ["construction", "debris", "بناء"],
        ["wood"] = ["wood", "خشب"],
        ["battery"] = ["battery", "batteries", "بطاريات"],
        ["tires"] = ["tire", "tyre", "إطارات", "تواير"],
        ["cooking-oil"] = ["cooking oil", "food oil", "زيوت الطبخ"],
        ["engine-oil"] = ["engine oil", "mineral oil", "زيت المحرك"],
        ["toys"] = ["toy", "ألعاب"],
        ["filters"] = ["filter", "filters", "فلاتر"],
        ["mixed"] = ["mixed", "multiple", "مختلطة", "متنوعة", "other", "أخرى"]
    };

    private static WasteSubmission ReadWasteSubmission(SqliteDataReader reader)
    {
        return new WasteSubmission(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            GetNullableString(reader, 2),
            GetNullableString(reader, 3),
            reader.GetString(4),
            GetNullableString(reader, 5),
            GetNullableString(reader, 6),
            reader.GetString(7),
            GetNullableString(reader, 8),
            GetNullableString(reader, 9),
            GetNullableString(reader, 10),
            reader.GetString(11),
            DateTimeOffset.Parse(reader.GetString(12)),
            [],
            []);
    }

    private static async Task<IReadOnlyList<WasteSubmission>> AddImagesToSubmissionsAsync(
        SqliteConnection connection,
        IReadOnlyList<WasteSubmission> submissions)
    {
        if (submissions.Count == 0)
        {
            return submissions;
        }

        var imagesBySubmissionId = new Dictionary<Guid, List<WasteSubmissionImage>>();
        foreach (var submission in submissions)
        {
            imagesBySubmissionId[submission.Id] = [];
        }

        var command = connection.CreateCommand();
        var parameterNames = new List<string>();
        for (var index = 0; index < submissions.Count; index++)
        {
            var parameterName = $"$id{index}";
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, submissions[index].Id.ToString());
        }

        command.CommandText = $"""
            SELECT waste_submission_id, file_name, content_type
            FROM waste_submission_images
            WHERE waste_submission_id IN ({string.Join(", ", parameterNames)})
            ORDER BY id;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var submissionId = Guid.Parse(reader.GetString(0));
            if (imagesBySubmissionId.TryGetValue(submissionId, out var images))
            {
                images.Add(new WasteSubmissionImage(
                    reader.GetString(1),
                    GetNullableString(reader, 2)));
            }
        }

        return submissions
            .Select(submission => submission with
            {
                Images = imagesBySubmissionId[submission.Id].ToArray()
            })
            .ToArray();
    }

    private static async Task<WasteSubmission?> ReadWasteSubmissionByIdAsync(SqliteConnection connection, Guid submissionId)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                submitter_type,
                submitter_name,
                company_name,
                phone,
                email,
                address,
                waste_category,
                quantity,
                notes,
                verification_token,
                status,
                created_at_utc,
                (
                    SELECT COUNT(*)
                    FROM waste_submission_images
                    WHERE waste_submission_id = waste_submissions.id
                ) AS image_count
            FROM waste_submissions
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", submissionId.ToString());

        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadWasteSubmission(reader) : null;
    }

    private static async Task InsertBidAsync(SqliteConnection connection, WasteBid bid)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO waste_bids (
                id,
                waste_submission_id,
                company_id,
                amount,
                pickup_date,
                notes,
                status,
                created_at_utc
            )
            VALUES (
                $id,
                $wasteSubmissionId,
                $companyId,
                $amount,
                $pickupDate,
                $notes,
                $status,
                $createdAtUtc
            );
            """;
        command.Parameters.AddWithValue("$id", bid.Id.ToString());
        command.Parameters.AddWithValue("$wasteSubmissionId", bid.WasteSubmissionId.ToString());
        command.Parameters.AddWithValue("$companyId", bid.CompanyId.ToString());
        command.Parameters.AddWithValue("$amount", bid.Amount);
        command.Parameters.AddWithValue("$pickupDate", ToDbValue(bid.PickupDate));
        command.Parameters.AddWithValue("$notes", ToDbValue(bid.Notes));
        command.Parameters.AddWithValue("$status", bid.Status);
        command.Parameters.AddWithValue("$createdAtUtc", bid.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<WasteBid?> ReadBidBySubmissionAndCompanyAsync(SqliteConnection connection, Guid submissionId, Guid companyId)
    {
        var command = connection.CreateCommand();
        command.CommandText = BidSelectSql + """

            WHERE wb.waste_submission_id = $submissionId
              AND wb.company_id = $companyId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$submissionId", submissionId.ToString());
        command.Parameters.AddWithValue("$companyId", companyId.ToString());

        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadBid(reader) : null;
    }

    private static async Task<WasteBid?> ReadBidByIdAsync(SqliteConnection connection, Guid bidId, Guid submissionId)
    {
        var command = connection.CreateCommand();
        command.CommandText = BidSelectSql + """

            WHERE wb.id = $bidId
              AND wb.waste_submission_id = $submissionId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$bidId", bidId.ToString());
        command.Parameters.AddWithValue("$submissionId", submissionId.ToString());

        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadBid(reader) : null;
    }

    private static async Task<IReadOnlyList<WasteBid>> ReadBidsForSubmissionAsync(SqliteConnection connection, Guid submissionId)
    {
        var command = connection.CreateCommand();
        command.CommandText = BidSelectSql + """

            WHERE wb.waste_submission_id = $submissionId
            ORDER BY
                CASE wb.status WHEN 'accepted' THEN 0 WHEN 'pending' THEN 1 ELSE 2 END,
                wb.pickup_date ASC,
                wb.created_at_utc DESC;
            """;
        command.Parameters.AddWithValue("$submissionId", submissionId.ToString());

        var bids = new List<WasteBid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            bids.Add(ReadBid(reader));
        }

        return bids;
    }

    private static async Task<WasteBid?> ReadAcceptedBidForSubmissionAsync(SqliteConnection connection, Guid submissionId)
    {
        var command = connection.CreateCommand();
        command.CommandText = BidSelectSql + """

            WHERE wb.waste_submission_id = $submissionId
              AND wb.status = 'accepted'
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$submissionId", submissionId.ToString());

        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadBid(reader) : null;
    }

    private static WasteBid ReadBid(SqliteDataReader reader)
    {
        return new WasteBid(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetDecimal(5),
            GetNullableString(reader, 6),
            GetNullableString(reader, 7),
            reader.GetString(8),
            DateTimeOffset.Parse(reader.GetString(9)));
    }

    private static async Task UpdateSubmissionStatusAsync(SqliteConnection connection, Guid submissionId, string status)
    {
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE waste_submissions SET status = $status WHERE id = $id;";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$id", submissionId.ToString());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpdateBidStatusAsync(SqliteConnection connection, Guid bidId, string status)
    {
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE waste_bids SET status = $status WHERE id = $id;";
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$id", bidId.ToString());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RejectOtherBidsAsync(SqliteConnection connection, Guid submissionId, Guid acceptedBidId)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE waste_bids
            SET status = 'rejected'
            WHERE waste_submission_id = $submissionId
              AND id <> $acceptedBidId;
            """;
        command.Parameters.AddWithValue("$submissionId", submissionId.ToString());
        command.Parameters.AddWithValue("$acceptedBidId", acceptedBidId.ToString());
        await command.ExecuteNonQueryAsync();
    }

    private const string BidSelectSql = """
        SELECT
            wb.id,
            wb.waste_submission_id,
            wb.company_id,
            c.company_name,
            c.email,
            wb.amount,
            wb.pickup_date,
            wb.notes,
            wb.status,
            wb.created_at_utc
        FROM waste_bids wb
        INNER JOIN companies c ON c.id = wb.company_id
        """;

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS companies (
                id TEXT NOT NULL PRIMARY KEY,
                company_name TEXT NOT NULL,
                mobile TEXT NOT NULL,
                email TEXT NOT NULL UNIQUE COLLATE NOCASE,
                address TEXT NULL,
                website TEXT NULL,
                logo_file_name TEXT NULL,
                created_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS waste_categories (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE COLLATE NOCASE
            );

            CREATE TABLE IF NOT EXISTS company_waste_categories (
                company_id TEXT NOT NULL,
                waste_category_id INTEGER NOT NULL,
                PRIMARY KEY (company_id, waste_category_id),
                FOREIGN KEY (company_id) REFERENCES companies(id) ON DELETE CASCADE,
                FOREIGN KEY (waste_category_id) REFERENCES waste_categories(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS otp_challenges (
                contact TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                purpose TEXT NOT NULL,
                code TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS waste_submissions (
                id TEXT NOT NULL PRIMARY KEY,
                submitter_type TEXT NOT NULL,
                submitter_name TEXT NULL,
                company_name TEXT NULL,
                phone TEXT NOT NULL,
                email TEXT NULL,
                address TEXT NULL,
                waste_category TEXT NOT NULL,
                quantity TEXT NULL,
                notes TEXT NULL,
                verification_token TEXT NULL,
                status TEXT NOT NULL DEFAULT 'received',
                created_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS waste_submission_images (
                id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                waste_submission_id TEXT NOT NULL,
                file_name TEXT NOT NULL,
                content_type TEXT NULL,
                created_at_utc TEXT NOT NULL,
                FOREIGN KEY (waste_submission_id) REFERENCES waste_submissions(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS waste_submission_targets (
                waste_submission_id TEXT NOT NULL,
                company_id TEXT NOT NULL,
                PRIMARY KEY (waste_submission_id, company_id),
                FOREIGN KEY (waste_submission_id) REFERENCES waste_submissions(id) ON DELETE CASCADE,
                FOREIGN KEY (company_id) REFERENCES companies(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS company_reviews (
                id TEXT NOT NULL PRIMARY KEY,
                company_id TEXT NOT NULL,
                waste_submission_id TEXT NULL,
                reviewer_name TEXT NOT NULL,
                reviewer_email TEXT NULL,
                rating INTEGER NOT NULL CHECK (rating >= 1 AND rating <= 5),
                comment TEXT NULL,
                created_at_utc TEXT NOT NULL,
                FOREIGN KEY (company_id) REFERENCES companies(id) ON DELETE CASCADE,
                FOREIGN KEY (waste_submission_id) REFERENCES waste_submissions(id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS waste_bids (
                id TEXT NOT NULL PRIMARY KEY,
                waste_submission_id TEXT NOT NULL,
                company_id TEXT NOT NULL,
                amount REAL NOT NULL,
                pickup_date TEXT NULL,
                notes TEXT NULL,
                status TEXT NOT NULL DEFAULT 'pending',
                created_at_utc TEXT NOT NULL,
                UNIQUE (waste_submission_id, company_id),
                FOREIGN KEY (waste_submission_id) REFERENCES waste_submissions(id) ON DELETE CASCADE,
                FOREIGN KEY (company_id) REFERENCES companies(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_company_waste_categories_category_id
                ON company_waste_categories(waste_category_id);

            CREATE INDEX IF NOT EXISTS ix_waste_submissions_phone
                ON waste_submissions(phone);

            CREATE INDEX IF NOT EXISTS ix_waste_submissions_status
                ON waste_submissions(status);

            CREATE INDEX IF NOT EXISTS ix_waste_submission_targets_company_id
                ON waste_submission_targets(company_id);

            CREATE INDEX IF NOT EXISTS ix_company_reviews_company_id_created_at
                ON company_reviews(company_id, created_at_utc DESC);

            CREATE INDEX IF NOT EXISTS ix_company_reviews_company_reviewer
                ON company_reviews(company_id, reviewer_email);

            CREATE INDEX IF NOT EXISTS ix_waste_bids_submission_status
                ON waste_bids(waste_submission_id, status);

            CREATE INDEX IF NOT EXISTS ix_waste_bids_company_status
                ON waste_bids(company_id, status);
            """;

        await command.ExecuteNonQueryAsync();
        await AddColumnIfMissingAsync(connection, "company_reviews", "waste_submission_id", "TEXT NULL");
        await AddColumnIfMissingAsync(connection, "waste_bids", "pickup_date", "TEXT NULL");

        var reviewOrderIndexCommand = connection.CreateCommand();
        reviewOrderIndexCommand.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_company_reviews_order_reviewer
                ON company_reviews(waste_submission_id, reviewer_email);
            """;
        await reviewOrderIndexCommand.ExecuteNonQueryAsync();
    }

    private static async Task AddColumnIfMissingAsync(SqliteConnection connection, string tableName, string columnName, string columnDefinition)
    {
        var infoCommand = connection.CreateCommand();
        infoCommand.CommandText = $"PRAGMA table_info({tableName});";

        await using (var reader = await infoCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        await alterCommand.ExecuteNonQueryAsync();
    }

    private async Task MigrateLegacyUsersAsync(SqliteConnection connection)
    {
        if (!File.Exists(legacyFilePath) || await CompanyCountAsync(connection) > 0)
        {
            return;
        }

        await using var stream = File.OpenRead(legacyFilePath);
        var users = await JsonSerializer.DeserializeAsync<List<CompanyUser>>(stream, JsonOptions) ?? [];

        foreach (var user in users)
        {
            if (!await EmailExistsAsync(connection, user.Email))
            {
                await InsertCompanyAsync(connection, user);
            }
        }
    }

    private static async Task<int> CompanyCountAsync(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM companies;";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> EmailExistsAsync(SqliteConnection connection, string email)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM companies WHERE email = $email LIMIT 1;";
        command.Parameters.AddWithValue("$email", email);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<CompanyUser?> FindByEmailAsync(SqliteConnection connection, string email)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, company_name, mobile, email, address, website, logo_file_name, created_at_utc
            FROM companies
            WHERE email = $email
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$email", email);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return await ReadCompanyAsync(connection, reader);
    }

    private static async Task<List<CompanyUser>> ReadUsersAsync(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, company_name, mobile, email, address, website, logo_file_name, created_at_utc
            FROM companies
            ORDER BY created_at_utc DESC;
            """;

        var users = new List<CompanyUser>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            users.Add(await ReadCompanyAsync(connection, reader));
        }

        return users;
    }

    private static async Task<CompanyUser> ReadCompanyAsync(SqliteConnection connection, SqliteDataReader reader)
    {
        var id = Guid.Parse(reader.GetString(0));
        return new CompanyUser(
            id,
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            GetNullableString(reader, 4),
            GetNullableString(reader, 5),
            GetNullableString(reader, 6),
            await ReadCategoriesAsync(connection, id),
            DateTimeOffset.Parse(reader.GetString(7)));
    }

    private static async Task<string[]> ReadCategoriesAsync(SqliteConnection connection, Guid companyId)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT wc.name
            FROM waste_categories wc
            INNER JOIN company_waste_categories cwc ON cwc.waste_category_id = wc.id
            WHERE cwc.company_id = $companyId
            ORDER BY wc.name;
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());

        var categories = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            categories.Add(reader.GetString(0));
        }

        return [.. categories];
    }

    private static async Task InsertCompanyAsync(SqliteConnection connection, CompanyUser user)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO companies (
                id,
                company_name,
                mobile,
                email,
                address,
                website,
                logo_file_name,
                created_at_utc
            )
            VALUES (
                $id,
                $companyName,
                $mobile,
                $email,
                $address,
                $website,
                $logoFileName,
                $createdAtUtc
            );
            """;

        command.Parameters.AddWithValue("$id", user.Id.ToString());
        command.Parameters.AddWithValue("$companyName", user.CompanyName);
        command.Parameters.AddWithValue("$mobile", user.Mobile);
        command.Parameters.AddWithValue("$email", user.Email);
        command.Parameters.AddWithValue("$address", ToDbValue(user.Address));
        command.Parameters.AddWithValue("$website", ToDbValue(user.Website));
        command.Parameters.AddWithValue("$logoFileName", ToDbValue(user.LogoFileName));
        command.Parameters.AddWithValue("$createdAtUtc", user.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync();

        foreach (var category in user.WasteCategories
            .Select(EmptyToNull)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var categoryId = await UpsertCategoryAsync(connection, category);
            await LinkCategoryAsync(connection, user.Id, categoryId);
        }
    }

    private static async Task<long> UpsertCategoryAsync(SqliteConnection connection, string category)
    {
        var insertCommand = connection.CreateCommand();
        insertCommand.CommandText = "INSERT OR IGNORE INTO waste_categories (name) VALUES ($name);";
        insertCommand.Parameters.AddWithValue("$name", category);
        await insertCommand.ExecuteNonQueryAsync();

        var selectCommand = connection.CreateCommand();
        selectCommand.CommandText = "SELECT id FROM waste_categories WHERE name = $name;";
        selectCommand.Parameters.AddWithValue("$name", category);
        return Convert.ToInt64(await selectCommand.ExecuteScalarAsync());
    }

    private static async Task LinkCategoryAsync(SqliteConnection connection, Guid companyId, long categoryId)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO company_waste_categories (company_id, waste_category_id)
            VALUES ($companyId, $categoryId);
            """;
        command.Parameters.AddWithValue("$companyId", companyId.ToString());
        command.Parameters.AddWithValue("$categoryId", categoryId);
        await command.ExecuteNonQueryAsync();
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static Guid? GetNullableGuid(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
    }

    private static object ToDbValue(string? value)
    {
        return value is null ? DBNull.Value : value;
    }

    private static string? EmptyToNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
