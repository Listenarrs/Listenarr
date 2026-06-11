# Listenarr AI Agent Instructions Index

This folder contains comprehensive instructions for AI assistants working with the Listenarr audiobook management system.

## Mandatory First Step

Before making code, dependency, workflow, or documentation changes, AI agents must review and follow:

- Repository contribution rules: [`../CONTRIBUTING.md`](../CONTRIBUTING.md)
- Backend architecture boundaries: [`../BACKEND_ARCHITECTURE.md`](../BACKEND_ARCHITECTURE.md)
- The primary AI guidance files listed below, especially [`copilot-instructions.md`](copilot-instructions.md), [`AGENTS.md`](AGENTS.md), and [`.cursorrules`](.cursorrules)

If these documents conflict, follow the more specific repository guidance first. In particular, keep infrastructure-shaped dependencies out of `listenarr.application`; add application-owned ports and implement adapters in infrastructure/API.

## Primary Reference Files

### [copilot-instructions.md](copilot-instructions.md) - **MOST COMPREHENSIVE**
Complete project documentation including:
- Full project structure and architecture
- Technology stack (.NET 10.0, Vue 3, TypeScript, Pinia, EF Core, SQLite, SignalR)
- Critical backend patterns (download status lifecycle, file existence validation, authentication)
- Critical frontend patterns (Pinia stores, performance optimization, type safety)
- API endpoints and development workflow
- Common troubleshooting scenarios
- Security considerations

**Use this file** for comprehensive project understanding and development guidance.

### [AGENTS.md](AGENTS.md) - **SECURITY FOCUSED**
Canonical secure coding guidelines for .NET and Vue.js:
- OWASP/CWE security patterns (SQL injection, XSS, CSRF, path traversal, etc.)
- Memory safety considerations for .NET
- Vue.js Composition API best practices
- Input validation and output encoding
- Secure configuration management

**Use this file** for security-focused development and OWASP compliance.

### [.cursorrules](.cursorrules) - **DEVELOPMENT PATTERNS**
Comprehensive coding standards and patterns:
- Backend architecture (service layer, repository pattern, DI, async/await)
- Frontend component structure (Composition API, Pinia stores)
- Critical backend patterns (download lifecycle, file validation, auth, job processing)
- Critical frontend patterns (state management, performance, type safety)
- Common troubleshooting scenarios
- Testing and documentation guidelines

**Use this file** for coding standards and architectural patterns.

## AI Provider-Specific Files

These files provide quick-start guidance tailored to specific AI providers, with references to the primary documentation:

### Major Providers
- **[ANTHROPIC.md](ANTHROPIC.md)** - Anthropic/Claude guidance
- **[CLAUDE.md](CLAUDE.md)** - Claude security patterns (full OWASP/CWE details)
- **[CLAUDE_LISTENARR.md](CLAUDE_LISTENARR.md)** - Claude Listenarr-specific guidance
- **[OpenAI.md](OpenAI.md)** - OpenAI/GPT guidance
- **[AZURE_OPENAI.md](AZURE_OPENAI.md)** - Azure OpenAI enterprise guidance

### Additional Providers
- **[BARD.md](BARD.md)** - Google Bard/Gemini guidance
- **[COHERE.md](COHERE.md)** - Cohere guidance
- **[HUGGINGFACE.md](HUGGINGFACE.md)** - Hugging Face guidance
- **[BEDROCK.md](BEDROCK.md)** - Amazon Bedrock guidance

### Tool-Specific Files
- **[clinerules](clinerules)** - Cline AI instructions
- **[windsurfrules](windsurfrules)** - Windsurf AI instructions (with file glob triggers)
- **[WARP.md](WARP.md)** - WARP terminal comprehensive guide

## Quick Start

1. **Before changing anything**: Read [`../CONTRIBUTING.md`](../CONTRIBUTING.md) and [`../BACKEND_ARCHITECTURE.md`](../BACKEND_ARCHITECTURE.md)
2. **For comprehensive project understanding**: Read [copilot-instructions.md](copilot-instructions.md)
3. **For security compliance**: Read [AGENTS.md](AGENTS.md)
4. **For coding standards**: Read [.cursorrules](.cursorrules)
5. **For provider-specific guidance**: Choose your AI provider file above

## Project Overview (Quick Reference)

**Listenarr** is a C# .NET 10.0 Web API backend with Vue.js 3 frontend for automated audiobook downloading and processing.

### Quick Start
```bash
npm run dev  # Start both API and frontend from repository root
```

### Key Technologies
- **Backend**: ASP.NET Core (.NET 10), Entity Framework Core, SQLite
- **Frontend**: Vue 3, TypeScript, Pinia, Vite, SignalR
- **Architecture**: Clean architecture (Domain, Application, Infrastructure layers)

### Critical Paths
- **Development database**: `.env/development/config/database/listenarr.db`
- **Development logs**: `.env/development/config/logs/listenarr-YYYYMMDD.log`
- **Backend**: http://localhost:4545
- **Frontend**: http://localhost:5173

### Most Common Issues
1. **Downloads not importing**: Check logs for auth errors (401, 409), verify 30s stability window
2. **Multiple databases**: Always run from repo root, not `bin/Debug`
3. **Hot reload fails**: Restart services with `npm run dev`

## File Organization

```
.github/
├── copilot-instructions.md    # MOST COMPREHENSIVE - Full project docs
├── AGENTS.md                   # SECURITY FOCUSED - OWASP/CWE patterns
├── .cursorrules                # DEVELOPMENT PATTERNS - Coding standards
├── RULES.md                    # This file - Navigation guide
├── CONVENTIONS.md              # Legacy conventions (see .cursorrules)
│
├── AI Provider Files (Quick-start with references)
│   ├── ANTHROPIC.md
│   ├── CLAUDE.md
│   ├── CLAUDE_LISTENARR.md
│   ├── OpenAI.md
│   ├── AZURE_OPENAI.md
│   ├── BARD.md
│   ├── COHERE.md
│   ├── HUGGINGFACE.md
│   └── BEDROCK.md
│
└── Tool-Specific Files
    ├── clinerules              # Cline AI
    ├── windsurfrules           # Windsurf AI
    └── WARP.md                 # WARP terminal

```

## Contributing

When updating AI instructions:
1. **Update primary files first**: copilot-instructions.md, AGENTS.md, .cursorrules
2. **Keep provider files concise**: They should reference primary files for details
3. **Test changes**: Ensure instructions work with target AI assistant
4. **Update this index**: Keep RULES.md current with new files

## Need Help?

- **Architecture questions**: See [copilot-instructions.md](copilot-instructions.md)
- **Security questions**: See [AGENTS.md](AGENTS.md)
- **Coding patterns**: See [.cursorrules](.cursorrules)
- **Troubleshooting**: Check "Common Issues" sections in any primary file
