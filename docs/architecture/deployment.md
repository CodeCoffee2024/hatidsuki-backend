# Deployment

Hatid Suki runs as two pieces:

- **API** on Railway, with a PostgreSQL database in the same Railway project.
- **Web app** on Vercel, served at https://hatidsuki.codekopi.com.

The web app forwards every `/api/...` request to the API (a rewrite in `vercel.json` in the web repository). The browser only ever talks to one address, so the login cookie stays first-party and no CORS setup is needed.

## 1. API on Railway

1. Create a Railway project and add a service from the `hatidsuki-backend` GitHub repository. Name the service `hatidsuki-backend`, because the deploy workflow uses that name.
2. Add a PostgreSQL database to the same project.
3. Set these variables on the API service:

   | Variable | Value |
   |---|---|
   | `DATABASE_URL` | `${{Postgres.DATABASE_URL}}` (a reference to the database) |
   | `Jwt__Key` | a random secret, for example `openssl rand -base64 48` |
   | `App__PublicBaseUrl` | `https://hatidsuki.codekopi.com` |
   | `Database__MigrateOnStartup` | `true` |
   | `ASPNETCORE_ENVIRONMENT` | `Production` |

   Railway sets `PORT` itself.
4. Under Settings, Networking, generate a public domain for the service. It looks like `hatidsuki-backend-production.up.railway.app`. You need it in the next part.
5. `railway.json` already points Railway at the Dockerfile and sets the health check to `/health/live`.

Check it: `https://<your-railway-domain>/health/live` should answer `{"status":"ok"}`.

## 2. Web app on Vercel

1. Import the `hatidsuki-frontend` repository in Vercel. The build settings come from `vercel.json`.
2. In `vercel.json`, replace `YOUR-API.up.railway.app` with the Railway domain from step 4 above, and push the change.
3. Under Settings, Domains, add `hatidsuki.codekopi.com`. At your DNS provider create the record Vercel shows (normally a `CNAME` for `hatidsuki` pointing at `cname.vercel-dns.com`).

The first person to register at `/register` becomes the owner of a new business. There is no demo data in production.

## 3. GitHub Actions

Both repositories have a `ci` workflow (build and test on pull requests) and a `deploy` workflow (test, then deploy on every push to `main`). The deploy steps are skipped until their secrets exist.

| Repository | Secret | Where to get it |
|---|---|---|
| `hatidsuki-backend` | `RAILWAY_TOKEN` | Railway, project settings, Tokens |
| `hatidsuki-frontend` | `VERCEL_TOKEN` | Vercel, account settings, Tokens |
| `hatidsuki-frontend` | `VERCEL_ORG_ID`, `VERCEL_PROJECT_ID` | `.vercel/project.json` after running `vercel link` in the web repository |

If you connect a repository to Railway or Vercel with their own GitHub integration, they deploy on every push without these secrets, and the `deploy` workflow can be removed.

## Things to know

- The Railway domain is public. The API counts rate limits by the forwarded client address, so someone calling the Railway domain directly can supply their own address header. Treat the limits as best effort.
- Migrations run when the API starts. That is fine for one instance. Run them as a separate step before scaling out.
- Set up database backups before real orders arrive. Check which options your Railway plan includes, or run `pg_dump` against `DATABASE_URL` on a schedule.
- Changing `Jwt__Key` signs everyone out.
- `ConnectionStrings__HatidSuki` and `App__FrontendBaseUrl` are accepted as aliases for `ConnectionStrings__Default` and `App__PublicBaseUrl` (some deploys were set up with those names).
- Demo seeding (`Seed__Demo`) is refused in Production unless `Seed__AllowInProduction=true` is also set — intentional, since the first person to register becomes the real owner.

## Optional: calling the API cross-origin instead of proxying

If the web app calls the Railway domain directly (not through the Vercel rewrite above), set `Cors__AllowedOrigins__0`, `__1`, … to the web app's origin(s). The API then sends real `Access-Control-Allow-Origin` headers and switches the refresh-token cookie to `SameSite=None` (only takes effect over HTTPS). Leave `Cors__AllowedOrigins` unset if you're using the same-origin proxy — it's the simpler default and needs nothing here.

## Optional: transactional email (Resend)

Set `Resend__ApiKey` and `Resend__FromAddress` (a verified sending address/domain in your Resend account) to turn on the order-confirmation email sent when a customer gives an email address. Without an API key, the API just logs instead of sending — nothing breaks, no emails go out. This is a lean slice of FS-020: there's no "ready" email yet, no retry queue, and no per-workspace template editor.
