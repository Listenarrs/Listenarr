# Backend Architecture Boundaries

Listenarr is moving toward a layered backend where each project has a clear job:

- `listenarr.domain` owns the domain model, value objects, domain exceptions, and business rules that do not need hosting, persistence, files, or network access.
- `listenarr.application` owns use-case orchestration, application services, DTOs, mapping, and contracts that other layers implement. It can coordinate work, but it should avoid owning persistence, file, network, parsing, or image-processing implementations.
- `listenarr.infrastructure` owns concrete adapters for technical concerns: EF Core and SQLite persistence, filesystem work, external HTTP clients, metadata/tagging libraries, HTML scraping/parsing, image inspection, cache implementations, SignalR infrastructure, and downloader integrations.
- `listenarr.api` is the composition and hosting layer. It wires dependency injection, controllers, middleware, Swagger/OpenAPI, auth policy, and request pipeline behavior.

## Current Decision

The diagram describes the intended boundary: application is business/use-case logic and infrastructure is persistence, files, and external adapters. The codebase is still in transition, but implementation-specific packages should be kept out of `listenarr.application` unless there is a documented reason to do otherwise.

New implementation-specific dependencies should go in `listenarr.infrastructure`. The application layer should define contracts and coordinate use cases; infrastructure should implement those contracts with EF Core, filesystem, HTTP, parsing, image, tagging, and other adapter libraries.

The application project should not reference SQLite providers, EF Core implementation packages, Swagger/OpenAPI packages, HTML parsers, image libraries, audio tagging libraries, ASP.NET Core hosting types, SignalR hubs, HTTP context, or data-protection implementations directly. SQLite and EF Core belong to infrastructure, Swagger/OpenAPI belongs to API, hosted adapters and SignalR delivery belong to infrastructure/API, and parsing/tagging/image inspection belong behind application ports implemented by infrastructure.

## Boundary Cleanup

The application layer now delegates these infrastructure-shaped concerns through interfaces:

- EF Core update failures are translated by infrastructure into application-owned `PersistenceException` types before they leave persistence.
- TagLibSharp ASIN writing is behind `IAudioTagWriter`, implemented by infrastructure.
- ImageSharp cover probing is behind `ICoverImageProbe`, implemented by infrastructure.
- HtmlAgilityPack text extraction and Audible author-page parsing are behind `IHtmlTextExtractor` and `IAudibleAuthorPageParser`, implemented by infrastructure.
- Hosted services and SignalR hubs live in infrastructure. Application code publishes client events through `IHubBroadcaster` instead of referencing hubs or `IHubContext`.
- HTTP request details are exposed to application services through `IRequestContextAccessor`, with ASP.NET Core adaptation handled outside application.
- Secret protection is exposed through `ISecretProtector`, with Data Protection implemented in infrastructure.
- `listenarr.application` no longer has an ASP.NET Core framework reference. It may reference general `Microsoft.Extensions.*` abstractions for logging, options, caching, dependency-factory access, and HTTP client factories, but it should not reference host/web implementation packages.

## Migration Direction

Use this pattern when moving a concern out of application:

1. Keep the application-level interface, DTOs, and result models in `listenarr.application` or `listenarr.domain`.
2. Move the concrete implementation to the appropriate `listenarr.infrastructure` feature or technology folder.
3. Register the implementation in `listenarr.infrastructure/Extensions/InfrastructureServiceRegistrationExtensions.cs`.
4. Keep `listenarr.api` responsible for calling the registration extension and composing the host.
5. Add or update focused tests before deleting the old implementation.

Recommended follow-up slices:

- Revisit background workers that combine orchestration with persistence or filesystem details and split the use case from the hosted adapter.
- Continue replacing direct service-locator patterns with narrower application ports where a worker or service only needs one operation from another layer.
- Keep new host-specific concerns in API or infrastructure and expose them to application through small application-owned contracts.

Until those slices are complete, reviewers should treat any new infrastructure-shaped application dependency as a boundary regression unless it is explicitly documented.
