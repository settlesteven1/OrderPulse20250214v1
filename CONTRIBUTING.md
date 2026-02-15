# Contributing to OrderPulse

## Development Setup

1. Install [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
2. Install [Azure Functions Core Tools v4](https://docs.microsoft.com/azure/azure-functions/functions-run-local)
3. Clone the repo and open `OrderPulse.sln` in your IDE
4. Copy `appsettings.Development.json.example` to `appsettings.Development.json` and fill in your values
5. Run database migrations against a local SQL Server instance

## Branch Strategy

- `main` — production-ready code, protected branch
- `develop` — integration branch for features
- `feature/*` — feature branches off `develop`
- `fix/*` — bug fixes

## Pull Request Process

1. Create a feature branch from `develop`
2. Make your changes with clear, focused commits
3. Ensure the solution builds: `dotnet build`
4. Open a PR targeting `develop`
5. Include a description of what changed and why

## Code Style

- Follow the `.editorconfig` settings
- Use C# 12 features where appropriate
- Prefer `record` types for DTOs
- Use `async/await` throughout — no `.Result` or `.Wait()`
- All public APIs need XML doc comments

## Commit Messages

Use conventional commit format:

```
feat: add return label PDF generation
fix: correct order status when partial shipment delivered
docs: update README with deployment steps
chore: update NuGet packages
```
