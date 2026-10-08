# DSMS Student Assistant (Agent)

An AI assistant for the **student portal** of the Digital School Management System. It explains how
the portal works (applying to programs, uploading the profile photo and documents, statuses) and looks at
the logged-in student's **own** profile, documents and applications to tell them exactly what is
missing and what to do next.

- **.NET 10 minimal API**, separate from `DigitalSchoolManagementSystem-BE`.
- **LLM provider is configurable**: Gemini (default), OpenAI or Claude, switched in `settings.json`.
- **Read-only**: the assistant never applies, uploads, edits or deletes anything. It guides; the student acts.

## How it works

```
FE AssistantWidget ──POST /agent/chat (student's JWT)──► Agent API
                                                          ├─ validates the JWT (same Jwt:Key/Issuer/Audience as the DSMS API), role = Student
                                                          ├─ Microsoft.Extensions.AI IChatClient (Gemini | OpenAI | Claude) + tool-calling loop
                                                          ├─ tools ──forward the same JWT──► DSMS API (GET only)
                                                          └─ Knowledge/portal-guide.md + Knowledge/document-requirements.json
```

| Tool | Data source |
|---|---|
| `get_my_profile` | `GET /api/students/me` |
| `list_programs` | `GET /api/programs` (+ my applications, for eligibility / already-applied) |
| `get_program_details` | `GET /api/programs/{id}` |
| `get_my_applications` | `GET /api/programs/me/applications` |
| `get_my_documents` | `GET /api/documents/me` |
| `get_required_documents` | `Knowledge/document-requirements.json` |
| `get_application_checklist` | All of the above → deterministic `ChecklistBuilder` |

Conversation history is kept in memory for each student and conversation. It expires after 30 idle minutes and is lost on restart.

## Configuration: `settings.json`

```jsonc
"Agent": { "Provider": "Gemini", ... },          // Gemini | OpenAI | Claude
"Providers": {
  "Gemini": { "Model": "gemini-3.8-flash", "ApiKey": "" },
  "OpenAI": { "Model": "gpt-5-mini",       "ApiKey": "" },
  "Claude": { "Model": "claude-sonnet-5-5","ApiKey": "" }
},
"Backend": { "BaseUrl": "http://localhost:5081/api" },
"Jwt": { "Issuer": "...", "Audience": "...", "Key": "" }   // must match the DSMS API
```

**To switch provider**, change `Agent:Provider` (or set the env var `Agent__Provider`) and restart. You only need the selected provider's API key.

**Never put API keys in `settings.json`.** Precedence is `settings.json` < `settings.{Environment}.json` <
user-secrets (Development) < environment variables < command line.

```bash
cd src/DigitalSchoolManagementSystem.Agent.Api
dotnet user-secrets set "Providers:Gemini:ApiKey" "<your-gemini-key>"
dotnet user-secrets set "Jwt:Key" "<same Jwt:Key as DigitalSchoolManagementSystem.API>"
```

Or with environment variables: `Providers__Gemini__ApiKey`, `Providers__OpenAI__ApiKey`, `Providers__Claude__ApiKey`, `Jwt__Key`.

- **Missing `Jwt:Key`:** the app refuses to start.
- **Missing key for the selected provider:** the app still starts, so a Docker stack comes up cleanly, but in an **unconfigured** state:
  - `/agent/health` reports `"status": "unconfigured"`.
  - Chat returns 503 with a friendly message.
  - The startup log names the variable to set.

  Add the key and restart the service.

## Document requirements

`Knowledge/document-requirements.json` decides which documents the assistant says are required. Rules merge in this order:
1. `default`
2. `byEducationLevel[program's eligible level]`
3. `byProgram[program name]`

When a later rule has the same `documentType`, it replaces the earlier one. The valid `documentType` values match the backend enum: `Passport`, `OfferLetter`, `Certificate`, `ProfilePhoto`, `Other`. The `byProgram` keys must match the program name exactly as staff created it (case-insensitive).

**The shipped content is a sensible default. Review it with the school before going live.** The backend does not enforce these rules. They are guidance for students and staff reviewers.

`Knowledge/portal-guide.md` is the portal how-to that grounds the assistant. If the UI changes, update this file too.

## Run locally

1. Start the DSMS API (`http://localhost:5081`).
2. Configure the keys (see above), then:
   ```bash
   dotnet run --project src/DigitalSchoolManagementSystem.Agent.Api     # http://localhost:5090
   ```
3. Start the FE (`npm run dev`). Its `.env.development` sets `VITE_AGENT_BASE_URL=http://localhost:5090/agent`.
4. Log in as a student and click **Ask Assistant** in the bottom-right corner.

Check the agent with `GET http://localhost:5090/agent/health`, which returns the provider and model and never shows keys.

## Docker

The root `docker-compose.yml` has an `agent` service, and nginx in the FE image proxies `/agent/` to it. Set these in the root `.env`:
`AGENT_PROVIDER` and `GEMINI_API_KEY` (or `OPENAI_API_KEY` / `ANTHROPIC_API_KEY`). `JWT_KEY` is shared with the API.

## API

| Method | Path | Auth | Body / result |
|---|---|---|---|
| POST | `/agent/chat` | Student JWT | `{ conversationId?, message }` → `{ conversationId, reply (markdown), actions: [{label, route}] }` |
| DELETE | `/agent/chat/{conversationId}` | Student JWT | Resets the conversation → 204 |
| GET | `/agent/health` | none | `{ status, provider, model }` |

Errors use `{ message }`, the same shape the FE's axios client already reads:
- 400: empty or too-long message
- 401/403: missing token or not a student
- 429: rate limit (20 per minute per student)
- 502: LLM provider failure
- 504: timeout

## Guardrails

- The system prompt limits answers to the portal and the student's own data. The assistant never invents requirements and suggests raising a Query with staff when it doesn't know.
- Tools are read-only and always use the student's own token, so the backend applies its normal authorization.
- Each turn has a cap on tool iterations, a message length limit, a 30 s timeout, and a per-student rate limit.
- Logs contain metadata only (provider, latency, tools called, token counts). Message text is never logged.

## Tests

```bash
dotnet test
```

The tests cover:
- the checklist rules (eligibility, deadline, missing/pending/rejected/approved documents, already applied)
- the knowledge-file merge and validation
- choosing the provider from settings, and the missing-key error
- the backend client (token forwarding, enum parsing, error handling)
- the full tool-calling loop over a scripted fake model
- the HTTP endpoints (auth, roles, validation)
