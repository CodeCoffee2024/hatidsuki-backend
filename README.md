# hatidsuki-backend

Backend for Hatid Suki, an ordering tool for small food and retail businesses. A business enters its item list, builds an order form and shares a QR code. Customers scan it and order, alone or for a group, and every person's order is tracked separately so nothing gets missed. The web app lives in the `hatidsuki-frontend` repository.

Built with .NET 9, ASP.NET Core, EF Core and PostgreSQL. The code is split into four layers:

```
src/HatidSuki.Domain          business rules, no framework dependencies
src/HatidSuki.Application     use cases (MediatR handlers, FluentValidation)
src/HatidSuki.Infrastructure  PostgreSQL, JWT, QR codes, demo data
src/HatidSuki.Api             HTTP controllers
tests/                        unit and persistence tests
```

## Running it locally

You need the .NET 9 SDK and PostgreSQL.

Create a database and user (the development settings expect port 5433):

```sql
CREATE ROLE hatidsuki LOGIN PASSWORD 'hatidsuki';
CREATE DATABASE hatidsuki OWNER hatidsuki;
```

Then start the API. In Development it applies the migrations and loads a demo bakery on first run.

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/HatidSuki.Api --launch-profile http
```

It listens on http://localhost:5168. The demo login is `demo@hatidsuki.local` with the password `HatidSuki1!`.

## Tests

```bash
dotnet test
```

## Configuration

| Setting | Meaning |
|---|---|
| `ConnectionStrings__Default` | PostgreSQL connection string |
| `DATABASE_URL` | Used when the setting above is empty, in the form `postgresql://user:password@host:port/database` (this is what Railway provides) |
| `Jwt__Key` | Secret used to sign login tokens, at least 32 characters. The API won't start without it |
| `App__PublicBaseUrl` | Public address of the site, used inside QR codes and tracking links |
| `Database__MigrateOnStartup` | `true` to apply migrations when the API starts |
| `Seed__Demo` | `true` loads the demo bakery. Leave it off in production |
| `PORT` | Port to listen on, set by most hosting platforms |

## Deploying

The API runs on Railway and the web app on Vercel. Steps are in [docs/architecture/deployment.md](docs/architecture/deployment.md).
