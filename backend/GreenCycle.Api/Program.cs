using System.Text.Json;
using System.Security.Cryptography;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

var apiRoot = FindApiRoot();

builder.Services.AddSingleton(new UserStore(apiRoot));
builder.Services.AddSingleton<OtpStore>();
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
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

app.MapPost("/api/waste/request-otp", async (WasteOtpRequest request, OtpStore otpStore, IEmailSender emailSender) =>
{
    if (string.IsNullOrWhiteSpace(request.Phone))
    {
        return Results.BadRequest(new ApiError("Phone number is required."));
    }

    if (string.IsNullOrWhiteSpace(request.Email))
    {
        return Results.BadRequest(new ApiError("Email is required so the OTP can be sent securely."));
    }

    var challenge = otpStore.Create(request.Phone);
    var emailResult = await emailSender.SendOtpAsync(request.Email, challenge.Code);
    if (!emailResult.Success)
    {
        return Results.Json(
            new ApiError(emailResult.ErrorMessage ?? "Email OTP is not configured on the server."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Ok(new OtpChallengeResponse(
        request.Email,
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

app.MapPost("/api/waste/submissions", async (HttpRequest request, UserStore store, OtpStore otpStore) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new ApiError("Waste submission must be sent as form data."));
    }

    var form = await request.ReadFormAsync();
    var submitterType = GetRequired(form, "submitterType");
    var phone = GetRequired(form, "phone");
    var wasteCategory = GetRequired(form, "wasteCategory");
    var verificationToken = GetRequired(form, "verificationToken");

    if (submitterType is null || phone is null || wasteCategory is null || verificationToken is null)
    {
        return Results.BadRequest(new ApiError("Submission type, phone number, waste category, and OTP verification are required."));
    }

    if (!otpStore.VerifyToken(phone, verificationToken))
    {
        return Results.BadRequest(new ApiError("Please verify the email OTP before submitting waste."));
    }

    var images = form.Files
        .Where(file => string.Equals(file.Name, "images", StringComparison.OrdinalIgnoreCase))
        .Select(file => new WasteSubmissionImage(file.FileName, file.ContentType))
        .ToArray();

    if (images.Length > 5)
    {
        return Results.BadRequest(new ApiError("A maximum of 5 waste images can be uploaded."));
    }

    var submission = new WasteSubmission(
        Guid.NewGuid(),
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
        images);

    await store.CreateWasteSubmissionAsync(submission);

    return Results.Created($"/api/waste/submissions/{submission.Id}", new WasteSubmissionResponse(
        submission.Id,
        submission.SubmitterType,
        submission.Phone,
        submission.WasteCategory,
        submission.Status,
        "Waste submission received."));
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

app.MapPost("/api/auth/request-otp", async (LoginRequest request, UserStore store, OtpStore otpStore, IEmailSender emailSender) =>
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

app.MapPost("/api/auth/login", async (LoginRequest request, UserStore store, OtpStore otpStore, IEmailSender emailSender) =>
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
        submission.Images.Length)));
});

app.Run();

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

public sealed record LoginRequest(string Email);

public sealed record OtpVerifyRequest(string Email, string Otp);

public sealed record WasteOtpRequest(string Phone, string? Email);

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
    WasteSubmissionImage[] Images);

public sealed record WasteSubmissionImage(string FileName, string? ContentType);

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
    string Message);

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
    int ImageCount);

public sealed record ApiError(string Message);

public sealed record EmailSendResult(bool Success, string? ErrorMessage = null);

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

    public bool VerifyToken(string contact, string token)
    {
        var normalizedContact = contact.Trim();

        lock (gate)
        {
            if (!verifiedTokens.TryGetValue(normalizedContact, out var storedToken))
            {
                return false;
            }

            return storedToken == token.Trim();
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

    public async Task CreateWasteSubmissionAsync(WasteSubmission submission)
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

            await transaction.CommitAsync();
        }
        finally
        {
            gate.Release();
        }
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
                    created_at_utc,
                    (
                        SELECT COUNT(*)
                        FROM waste_submission_images
                        WHERE waste_submission_id = waste_submissions.id
                    ) AS image_count
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
                    new WasteSubmissionImage[reader.GetInt32(13)]));
            }

            return submissions;
        }
        finally
        {
            gate.Release();
        }
    }

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

            CREATE INDEX IF NOT EXISTS ix_company_waste_categories_category_id
                ON company_waste_categories(waste_category_id);

            CREATE INDEX IF NOT EXISTS ix_waste_submissions_phone
                ON waste_submissions(phone);

            CREATE INDEX IF NOT EXISTS ix_waste_submissions_status
                ON waste_submissions(status);
            """;

        await command.ExecuteNonQueryAsync();
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
