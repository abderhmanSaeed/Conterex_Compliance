# 07 — Architecture Diagrams

> Mermaid diagrams describing the **current** architecture (anchored to the actual codebase) and **recommended** future flows (clearly labelled). All diagrams render in any markdown viewer that supports Mermaid (GitHub, GitLab, VS Code preview, Obsidian, etc.).

---

## 1. Solution / project dependency graph

The arrows below come directly from `<ProjectReference>` declarations in each `.csproj`.

```mermaid
graph LR
    Web[Web<br/><i>composition root</i><br/><b>net6.0 / aspnetcore</b>]
    Presentation[Presentation<br/><i>controllers</i>]
    Application[Application<br/><i>CQRS, validators</i>]
    Infrastructure[Infrastructure<br/><i>EF Core, repositories</i>]
    Domain[Domain<br/><i>entities, abstractions</i>]

    Web --> Presentation
    Web --> Application
    Web --> Infrastructure
    Web --> Domain

    Presentation --> Application
    Application --> Domain
    Infrastructure --> Domain

    style Domain fill:#cfe7ff,stroke:#000,stroke-width:2px,color:#000
    style Application fill:#d6f0d6,stroke:#000,color:#000
    style Infrastructure fill:#ffe6cc,stroke:#000,color:#000
    style Presentation fill:#fff2b3,stroke:#000,color:#000
    style Web fill:#ffd6e0,stroke:#000,stroke-width:2px,color:#000
```

**Key observations:**
- Domain is a leaf node (depends on nothing).
- Application and Infrastructure both depend only on Domain — they don't see each other, which is the canonical Clean Architecture variant.
- Web is the only project that knows about all four.
- *Recommended change* (per File 06 §5): introduce `Infrastructure → Application` so Infrastructure can implement Application-defined abstractions like `ISqlConnectionFactory` and `IEmailSender`. This is the standard Clean Architecture refinement.

---

## 2. Clean Architecture rings (conceptual)

```mermaid
graph TB
    subgraph Domain ["Domain Layer (inner)"]
        D1[Entities: Webinar]
        D2[Primitives: Entity]
        D3[Abstractions: IWebinarRepository, IUnitOfWork]
        D4[Exceptions: WebinarNotFoundException]
    end

    subgraph Application ["Application Layer"]
        A1[Commands: CreateWebinarCommand]
        A2[Queries: GetWebinarByIdQuery]
        A3[Handlers: internal sealed]
        A4[Validators: FluentValidation]
        A5[Behaviors: ValidationBehavior]
        A6[Messaging: ICommand, IQuery]
    end

    subgraph Infrastructure ["Infrastructure Layer"]
        I1[ApplicationDbContext]
        I2[WebinarConfiguration]
        I3[WebinarRepository]
        I4[Migrations]
    end

    subgraph Presentation ["Presentation Layer"]
        P1[ApiController]
        P2[WebinarsController]
    end

    subgraph Web ["Web - Composition Root"]
        W1[Program.cs]
        W2[Startup.cs]
        W3[ExceptionHandlingMiddleware]
        W4[appsettings.json]
        W5[Dockerfile]
    end

    Web --> Presentation
    Web --> Infrastructure
    Web --> Application
    Web --> Domain
    Presentation --> Application
    Application --> Domain
    Infrastructure --> Domain

    style Domain fill:#cfe7ff,color:#000
    style Application fill:#d6f0d6,color:#000
    style Infrastructure fill:#ffe6cc,color:#000
    style Presentation fill:#fff2b3,color:#000
    style Web fill:#ffd6e0,color:#000
```

---

## 3. Write-path request flow (POST /api/webinars)

```mermaid
sequenceDiagram
    autonumber
    actor Client
    participant Kestrel as ASP.NET Kestrel
    participant ExMw as ExceptionHandlingMiddleware<br/>(Web)
    participant Routing as UseRouting / UseAuthorization
    participant Controller as WebinarsController<br/>(Presentation)
    participant ISender as ISender (MediatR)
    participant ValBeh as ValidationBehavior<br/>(Application)
    participant Validator as CreateWebinarCommandValidator
    participant Handler as CreateWebinarCommandHandler
    participant Repo as IWebinarRepository<br/>→ WebinarRepository
    participant DbCtx as ApplicationDbContext<br/>(IUnitOfWork)
    participant Postgres as PostgreSQL

    Client->>Kestrel: POST /api/webinars { Name, ScheduledOn }
    Kestrel->>ExMw: HttpContext
    ExMw->>Routing: next()
    Routing->>Controller: bind body to CreateWebinarRequest
    Controller->>Controller: request.Adapt<CreateWebinarCommand>() (Mapster)
    Controller->>ISender: Send(command, ct)
    ISender->>ValBeh: dispatch through pipeline
    ValBeh->>Validator: Validate(command)
    Validator-->>ValBeh: errors[] (empty if valid)
    alt validation passes
        ValBeh->>Handler: Handle(command, ct)
        Handler->>Repo: Insert(new Webinar(...))
        Repo->>DbCtx: Set<Webinar>().Add(entity)
        Handler->>DbCtx: SaveChangesAsync(ct) via IUnitOfWork
        DbCtx->>Postgres: INSERT INTO Webinars VALUES (...)
        Postgres-->>DbCtx: 1 row affected
        DbCtx-->>Handler: SaveChanges result
        Handler-->>ValBeh: webinar.Id (Guid)
        ValBeh-->>ISender: Guid
        ISender-->>Controller: Guid
        Controller-->>Client: 201 Created, Location header
    else validation fails
        ValBeh->>ValBeh: throw ValidationException(errors)
        ValBeh-->>ExMw: bubble up
        ExMw->>ExMw: switch exception type → 400
        ExMw-->>Client: 400 Bad Request, { message, errors[] }
    end
```

---

## 4. Read-path request flow (GET /api/webinars/{id})

```mermaid
sequenceDiagram
    autonumber
    actor Client
    participant Kestrel as ASP.NET Kestrel
    participant ExMw as ExceptionHandlingMiddleware
    participant Controller as WebinarsController
    participant ISender as ISender (MediatR)
    participant ValBeh as ValidationBehavior
    participant Handler as GetWebinarQueryHandler
    participant DbConn as IDbConnection<br/>(borrowed from ApplicationDbContext)
    participant Postgres as PostgreSQL

    Client->>Kestrel: GET /api/webinars/{webinarId}
    Kestrel->>ExMw: HttpContext
    ExMw->>Controller: next()
    Controller->>ISender: Send(new GetWebinarByIdQuery(id), ct)
    Note over ValBeh: ValidationBehavior is constrained to ICommand and is skipped for IQuery
    ISender->>Handler: Handle(query, ct)
    Handler->>DbConn: QueryFirstOrDefaultAsync<WebinarResponse>(SQL)
    DbConn->>Postgres: SELECT * FROM "Webinars" WHERE "Id"=@WebinarId
    Postgres-->>DbConn: row or null
    DbConn-->>Handler: WebinarResponse or null

    alt row found
        Handler-->>ISender: WebinarResponse
        ISender-->>Controller: WebinarResponse
        Controller-->>Client: 200 OK, payload
    else not found
        Handler->>Handler: throw new WebinarNotFoundException(id)
        Handler-->>ExMw: bubble up
        ExMw->>ExMw: NotFoundException → 404
        ExMw-->>Client: 404 Not Found
    end
```

> **Issue called out earlier (File 03 §5):** the read path injects `System.Data.IDbConnection` directly into the Application handler and embeds raw SQL. This couples Application to Infrastructure. The "Recommended read path" diagram below shows the target shape after the fix.

---

## 5. Recommended read-path flow (post File 03 §5 fix)

```mermaid
sequenceDiagram
    autonumber
    actor Client
    participant Controller as WebinarsController
    participant ISender as ISender (MediatR)
    participant Handler as GetWebinarQueryHandler
    participant ReadRepo as IWebinarReadRepository<br/>(Application abstraction)
    participant ReadImpl as WebinarReadRepository<br/>(Infrastructure)
    participant Factory as ISqlConnectionFactory
    participant Postgres as PostgreSQL

    Client->>Controller: GET /api/webinars/{id}
    Controller->>ISender: Send(GetWebinarByIdQuery(id), ct)
    ISender->>Handler: Handle(query, ct)
    Handler->>ReadRepo: GetByIdAsync(id, ct)
    ReadRepo->>ReadImpl: (DI: registered impl)
    ReadImpl->>Factory: CreateOpenConnection()
    Factory-->>ReadImpl: Open IDbConnection
    ReadImpl->>Postgres: SELECT Id, Name, ScheduledOn FROM "Webinars" WHERE Id=@Id
    Postgres-->>ReadImpl: row or null
    ReadImpl-->>Handler: WebinarResponse?
    alt found
        Handler-->>Controller: WebinarResponse
        Controller-->>Client: 200 OK
    else not found
        Handler-->>Controller: throw WebinarNotFoundException
        Controller-->>Client: 404 Not Found (via ExceptionHandlingMiddleware)
    end
```

Application code no longer imports `System.Data`. SQL lives entirely in Infrastructure.

---

## 6. Authentication flow

### Current state — none

```mermaid
graph LR
    Req[HTTP Request] --> RouteAuth[UseRouting → UseAuthorization]
    RouteAuth --> Endpoint[Controller endpoint]
    Endpoint --> Anywhere[Always anonymous - no authentication, no checks]

    style Anywhere fill:#ffcccc,color:#000
```

**Reality:** every endpoint is publicly accessible. `app.UseAuthorization()` in `Startup.cs:85` runs but has nothing to enforce because `services.AddAuthentication()` was never called.

### Recommended JWT bearer flow (after File 03 §2)

```mermaid
sequenceDiagram
    autonumber
    actor User
    participant Client as Client (SPA / mobile)
    participant Auth as Identity Provider<br/>(e.g. Auth0 / Keycloak / Azure AD)
    participant API as Web API<br/>(this codebase)
    participant Endpoint as Protected Endpoint

    User->>Client: enters credentials
    Client->>Auth: POST /oauth/token (grant=password OR auth_code)
    Auth-->>Client: { access_token (JWT), refresh_token, expires_in }
    Client->>Client: store tokens securely

    loop API requests
        Client->>API: GET /api/webinars/{id}<br/>Authorization: Bearer <jwt>
        API->>API: UseAuthentication validates JWT (signature, issuer, audience, exp)
        alt token valid
            API->>API: HttpContext.User populated with claims
            API->>API: UseAuthorization checks [Authorize] / policies
            alt authorized
                API->>Endpoint: invoke controller method
                Endpoint-->>Client: 200 OK
            else not authorized
                API-->>Client: 403 Forbidden
            end
        else token invalid / expired
            API-->>Client: 401 Unauthorized
            Client->>Auth: POST /oauth/token (grant=refresh_token)
            Auth-->>Client: new access_token
            Note over Client: retry request
        end
    end
```

---

## 7. Database interaction flows — write vs read (current)

```mermaid
flowchart TB
    subgraph WritePath["Write path (Command)"]
        direction TB
        WC[CreateWebinarCommandHandler]
        WR[IWebinarRepository]
        WI[WebinarRepository<br/>impl in Infrastructure]
        WD[ApplicationDbContext<br/>change tracker]
        WS[SaveChangesAsync<br/>via IUnitOfWork]
        WP[(PostgreSQL)]
        WC --> WR --> WI --> WD
        WC --> WS --> WD
        WD --> WP
    end

    subgraph ReadPath["Read path (Query)"]
        direction TB
        RH[GetWebinarQueryHandler<br/>in Application]
        RC[IDbConnection<br/>System.Data leak]
        RS[raw SQL string in handler]
        RP[(PostgreSQL)]
        RH --> RC
        RH --> RS
        RC --> RP
    end

    style WI fill:#d6f0d6,color:#000
    style RH fill:#ffcccc,color:#000
    style RC fill:#ffcccc,color:#000
    style RS fill:#ffcccc,color:#000
```

Two patterns, two layers of code that the read side genuinely should not be touching. File 03 §5 fixes the read path to mirror the write path's structure.

---

## 8. Integration flow (recommended pattern — not currently in the code)

```mermaid
sequenceDiagram
    autonumber
    participant Handler as Application Handler
    participant IEmail as IEmailSender<br/>(Application abstraction)
    participant SendGrid as SendGridEmailSender<br/>(Infrastructure impl)
    participant Polly as Polly retry policy
    participant Http as HttpClientFactory
    participant Vendor as SendGrid API

    Handler->>IEmail: SendAsync(message, ct)
    IEmail->>SendGrid: (DI: typed HttpClient)
    SendGrid->>Polly: invoke with policy
    Polly->>Http: GetHttpClient()
    Http->>Vendor: POST /v3/mail/send (with Authorization header)
    alt 2xx
        Vendor-->>Http: 202 Accepted
        Http-->>Polly: success
        Polly-->>SendGrid: complete
        SendGrid-->>Handler: void
    else transient failure
        Vendor-->>Http: 5xx / timeout
        Http-->>Polly: failure
        Polly->>Polly: wait + retry (up to N times)
        Polly->>Http: retry POST
    else permanent failure
        Polly-->>SendGrid: HttpRequestException
        SendGrid-->>Handler: throws
        Handler-->>Caller: bubble up
    end
```

---

## 9. Background-job flow (recommended Hangfire pattern)

```mermaid
flowchart LR
    subgraph WebApp["Web API process"]
        Trigger[Recurring schedule / Command enqueue]
        Storage[(Hangfire tables<br/>in PostgreSQL)]
        Trigger -- writes job --> Storage
    end

    subgraph HangfireServer["Hangfire Server (in-process)"]
        Worker[BackgroundJobServer thread pool]
        JobImpl[ISendUpcomingWebinarRemindersJob<br/>impl in Infrastructure]
    end

    subgraph Dependencies["Job dependencies"]
        ReadRepo[IWebinarReadRepository]
        Email[IEmailSender]
    end

    DB[(PostgreSQL<br/>application schema)]
    Vendor[Email vendor]

    Storage -- poll for due jobs --> Worker
    Worker -- resolve via DI --> JobImpl
    JobImpl --> ReadRepo --> DB
    JobImpl --> Email --> Vendor
    JobImpl -- mark success/fail --> Storage

    style Trigger fill:#fff2b3,color:#000
    style Worker fill:#d6f0d6,color:#000
    style JobImpl fill:#d6f0d6,color:#000
```

---

## 10. Outbox / domain-event flow (recommended, for cross-context async events)

```mermaid
sequenceDiagram
    autonumber
    participant Handler as Command Handler
    participant Aggregate as Aggregate Root (Webinar)
    participant DbCtx as ApplicationDbContext.SaveChangesAsync
    participant Outbox as OutboxMessages table
    participant Publisher as MediatR IPublisher (in-process)
    participant Worker as Outbox Worker (BackgroundService)
    participant Bus as Message Bus<br/>(RabbitMQ / Service Bus)

    Handler->>Aggregate: invoke domain method (e.g. Create / Cancel)
    Aggregate->>Aggregate: raise WebinarCreatedDomainEvent
    Handler->>DbCtx: SaveChangesAsync(ct)
    DbCtx->>DbCtx: collect aggregate.DomainEvents
    DbCtx->>Outbox: INSERT OutboxMessages(...)<br/>same transaction as aggregate change
    DbCtx-->>Handler: commit
    DbCtx->>Publisher: Publish(domainEvent) — in-process side effects
    Publisher-->>Handler: notification handlers complete

    Note over Worker: periodically (BackgroundService)
    Worker->>Outbox: SELECT * FROM OutboxMessages WHERE ProcessedOn IS NULL FOR UPDATE SKIP LOCKED
    Worker->>Bus: publish integration event
    Bus-->>Worker: ack
    Worker->>Outbox: UPDATE ProcessedOn = NOW()
```

Guarantees:
- Aggregate change and Outbox row are **atomic** (same DB transaction).
- Integration event delivery is **at-least-once** — consumers must be idempotent.
- In-process notification handlers run **after commit**, never before — preventing "event fired but data not saved" anomalies.

---

## 11. Layered responsibility heatmap (current state)

```mermaid
graph TB
    subgraph DomainOK["Domain — clean"]
        DOK[no leakage<br/>9 source files<br/>0 NuGet]
    end
    subgraph ApplicationMixed["Application — mostly clean"]
        AOK[CQRS, validators, behaviors<br/>15 source files]
        AISSUE[IDbConnection leakage<br/>in GetWebinarQueryHandler]
    end
    subgraph InfrastructureOK["Infrastructure — clean"]
        IOK[DbContext, configs, repo<br/>8 source files]
        ILEGACY[Migrations namespace<br/>Persistence.Migrations leftover]
    end
    subgraph PresentationOK["Presentation — clean"]
        POK[Thin controllers<br/>3 source files]
    end
    subgraph WebMixed["Web — needs work"]
        WOK[Pipeline order correct<br/>Exception middleware exists]
        WISSUE1[Secrets in appsettings.Development.json]
        WISSUE2[UseAuthorization without AddAuthentication]
        WISSUE3[Migrations on startup]
        WISSUE4[Monolithic Startup.cs]
        WISSUE5[Legacy hosting pattern on net6]
        WISSUE6[Missing health checks / CORS / logging]
    end

    style DOK fill:#d6f0d6,color:#000
    style AOK fill:#d6f0d6,color:#000
    style IOK fill:#d6f0d6,color:#000
    style POK fill:#d6f0d6,color:#000
    style WOK fill:#d6f0d6,color:#000
    style AISSUE fill:#fff2b3,color:#000
    style ILEGACY fill:#fff2b3,color:#000
    style WISSUE1 fill:#ffcccc,color:#000
    style WISSUE2 fill:#ffcccc,color:#000
    style WISSUE3 fill:#ffcccc,color:#000
    style WISSUE4 fill:#fff2b3,color:#000
    style WISSUE5 fill:#fff2b3,color:#000
    style WISSUE6 fill:#fff2b3,color:#000
```

Green: clean. Yellow: medium concern. Red: critical.

---

## 12. Module decomposition (recommended target — modular monolith)

Forward-looking. When the codebase grows to multiple aggregates, organize as feature modules:

```mermaid
graph TB
    subgraph Modules["Application Modules"]
        WMod[Webinars Module<br/>Domain.Webinars<br/>Application.Webinars<br/>Infrastructure.Webinars]
        SMod[Speakers Module]
        RMod[Registrations Module]
        AMod[Identity Module]
    end

    subgraph Shared["Shared Kernel"]
        SK1[Domain.Primitives<br/>Entity, AggregateRoot, IDomainEvent]
        SK2[Application.Abstractions<br/>IDateTimeProvider, ISqlConnectionFactory]
        SK3[Infrastructure.Common<br/>ApplicationDbContext, NpgsqlConnectionFactory]
    end

    Host[Web - Composition Root]

    WMod --> SK1
    WMod --> SK2
    WMod --> SK3
    SMod --> SK1
    SMod --> SK2
    SMod --> SK3
    RMod --> SK1
    RMod --> SK2
    RMod --> SK3
    AMod --> SK1
    AMod --> SK2
    AMod --> SK3

    Host --> WMod
    Host --> SMod
    Host --> RMod
    Host --> AMod

    WMod -.domain events.-> RMod
    RMod -.domain events.-> WMod
    AMod -.domain events.-> RMod

    style Host fill:#ffd6e0,color:#000
    style Shared fill:#cfe7ff,color:#000
    style WMod fill:#d6f0d6,color:#000
    style SMod fill:#d6f0d6,color:#000
    style RMod fill:#d6f0d6,color:#000
    style AMod fill:#d6f0d6,color:#000
```

Each module is its own folder layout (with internal Domain / Application / Infrastructure sub-namespaces) and communicates with peers **only** via shared domain events or a publicly defined module API. This is the realistic next step after the foundational fixes in Files 03-04. See File 04 §12 for the full evolution.

---

## 13. Where each diagram lives in the file system

| Diagram | Relates to |
|---------|------------|
| §1 — Solution dependency graph | every `.csproj` |
| §2 — Clean Architecture rings | layering principle |
| §3 — Write-path flow | `WebinarsController.CreateWebinar`, `CreateWebinarCommandHandler`, `WebinarRepository`, `ApplicationDbContext` |
| §4 — Read-path flow (current) | `WebinarsController.GetWebinar`, `GetWebinarQueryHandler` (with the `IDbConnection` leak) |
| §5 — Read-path flow (recommended) | post-refactor `IWebinarReadRepository` / `ISqlConnectionFactory` |
| §6 — Authentication flow | currently empty; recommended JWT path |
| §7 — DB interaction (current) | mixed write/read patterns |
| §8 — Integration flow (recommended) | typed HttpClients + Polly |
| §9 — Background-job flow (recommended) | future Hangfire adoption |
| §10 — Outbox flow (recommended) | future cross-context events |
| §11 — Responsibility heatmap | composite of all audit findings |
| §12 — Modular monolith target | future state |
