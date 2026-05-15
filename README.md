# GreenCycle

Static website files plus a .NET 10 backend for login, recycling company signup, OTP verification, and local SQLite data storage.

## Run Locally

```powershell
dotnet run --project backend/GreenCycle.Api/GreenCycle.Api.csproj
```

Open the URL printed by .NET, usually `http://localhost:5294`.

## Backend

- `POST /api/auth/signup` registers a recycling company from the signup modal.
- `POST /api/auth/request-otp` sends a login OTP to the registered email address.
- `POST /api/auth/verify-otp` verifies OTP and returns the browser session data.
- `POST /api/auth/login` is kept as a compatibility alias for requesting an OTP.
- `POST /api/waste/request-otp` sends a waste submission OTP to the entered email address.
- `POST /api/waste/verify-otp` verifies the waste submission OTP and unlocks the submit button.
- `POST /api/waste/submissions` stores a verified waste submission in SQLite.
- `GET /api/companies` lists registered companies.
- Local application data is stored in `backend/GreenCycle.Api/App_Data/greencycle.db`.
- Existing `backend/GreenCycle.Api/App_Data/users.json` signup data is migrated into SQLite automatically the first time the backend starts.

## Database

The backend creates the SQLite database and tables automatically on startup. The schema includes:

- `companies` for registered recycling company accounts.
- `waste_categories` for reusable waste category names.
- `company_waste_categories` for company-to-category relationships.
- `otp_challenges` for OTP challenge persistence support.
- `waste_submissions` for individual or corporate waste submission records.
- `waste_submission_images` for uploaded waste submission image metadata.

## Email OTP Setup

Configure SMTP before testing login OTP email delivery:

```powershell
dotnet user-secrets init --project backend/GreenCycle.Api/GreenCycle.Api.csproj
dotnet user-secrets set "Smtp:Host" "smtp.example.com" --project backend/GreenCycle.Api/GreenCycle.Api.csproj
dotnet user-secrets set "Smtp:Port" "587" --project backend/GreenCycle.Api/GreenCycle.Api.csproj
dotnet user-secrets set "Smtp:Username" "smtp-user" --project backend/GreenCycle.Api/GreenCycle.Api.csproj
dotnet user-secrets set "Smtp:Password" "smtp-password" --project backend/GreenCycle.Api/GreenCycle.Api.csproj
dotnet user-secrets set "Smtp:FromEmail" "no-reply@example.com" --project backend/GreenCycle.Api/GreenCycle.Api.csproj
```

The OTP is emailed only. It is not returned to the browser or displayed on screen.

For local `appsettings.json` setup, copy `backend/GreenCycle.Api/appsettings.example.json` to `backend/GreenCycle.Api/appsettings.json` and replace the placeholder SMTP values. The real `appsettings.json` file is ignored by Git so private SMTP passwords stay local.

## Published Website API Setup

GitHub Pages only hosts the static website. OTP email requires the .NET backend to be deployed separately to a public HTTPS URL. After deploying the backend, set that URL in `site-config.js`:

```js
window.GreenCycleConfig = {
  apiBaseUrl: "https://your-greencycle-api.example.com"
};
```

Also allow the GitHub Pages origin in the backend `Cors:AllowedOrigins` setting.
