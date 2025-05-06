# LLM API Proxy and Manager

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET Version](https://img.shields.io/badge/.NET-8.0-blueviolet)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Language](https://img.shields.io/badge/Language-C%23%2012-blue)](https://learn.microsoft.com/en-us/dotnet/csharp/)

The LLM API Proxy and Manager is a C#-based ASP.NET Core application designed to serve as an intelligent intermediary between LLM API clients (e.g., frontend apps, other services) and various OpenAI-compatible backend providers. It centralizes request routing, load balancing, API key management, and error handling, abstracting backend complexities from the end user and ensuring a seamless, uninterrupted experience.

## 🚀 Purpose

*   **Aggregate Multiple LLM Backends:** Consolidate access to various OpenAI-compatible APIs (e.g., OpenAI, OpenRouter, Google Gemini, Mistral, DeepSeek, local LM Studio instances).
*   **Dynamic Request Routing:** Intelligently route incoming requests to one or more configured backend providers based on defined strategies.
*   **Load Balancing & Failover:** Distribute load across multiple API keys for a single backend or across different backends. Automatically failover to backup backends or keys in case of errors.
*   **Robust Streaming Support:** Full, zero-latency pass-through support for `stream=true` (text/event-stream) responses.
*   **Error Abstraction:** Suppress transient backend errors (like rate limits or temporary server issues) from reaching the client. Only fails if all configured backends for a request are unavailable.
*   **Centralized Configuration:** Manage backend providers, API keys, and routing strategies through a simple web-based admin interface.
*   **Observability:** Provides logging for request routing decisions, backend errors, and retry attempts.

## ✨ Key Features

*   **Supported Routing Strategies:**
    *   **Failover:** Prioritize backends/keys in a specific order, falling back only on failure.
    *   **Round Robin:** Cycle through available backends/keys sequentially for each request.
    *   **Weighted Distribution:** Route traffic to backends according to configured weights (experimental, best with external state for true distribution).
*   **Provider Multiplexing:** Seamlessly combine and switch between different LLM providers.
*   **Backend-Specific Model Names:** Map a general model name requested by the client to a specific model name required by a particular backend (e.g., client requests `phi-3-mini`, proxy sends `phi-3-mini-gguf` to one backend and `microsoft/phi-3-mini-128k-instruct` to another).
*   **Streaming Proxy:** Efficiently proxies `text/event-stream` responses with no buffering or transformation. Failover for streaming requests happens only before the first token is received.
*   **Fail-Safe User Experience:** Hides backend failures from the client unless all configured backends for a given model request are down.
*   **Dynamic Configuration:**
    *   Manage LLM models, their backends, API keys, and routing strategies via a web UI (`/admin`).
    *   Configuration is persisted in a local `dynamic_routing.json` file.
    *   Default configurations are loaded on first run if the JSON file is missing.
*   **Standard OpenAI-Compatible Endpoints:**
    *   `POST /v1/chat/completions`
    *   `POST /v1/completions` (Legacy)
    *   `POST /v1/embeddings`
    *   `GET /v1/models` (Lists models configured in the proxy)
*   **Health Check:** `GET /health` endpoint.

## 🛠️ Technical Stack

*   **Language:** C# 12
*   **Framework:** .NET 8 (ASP.NET Core Minimal API)
*   **Target Platform:** Cross-platform (Windows, Linux, macOS)
*   **Key Dependencies:**
    *   `System.Net.Http`
    *   `System.Text.Json`
    *   `Microsoft.Extensions.Logging`
    *   `Microsoft.Extensions.Configuration`

## ⚙️ Getting Started

### Prerequisites

*   [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) installed.
*   (Optional) Git for cloning the repository.

### Installation & Running

1.  **Clone the repository (if applicable):**
    ```bash
    git clone <repository-url>
    cd LLMProxy
    ```
2.  **Restore Dependencies:**
    ```bash
    dotnet restore
    ```
3.  **Configure Port (Optional):**
    The application is configured by default to run on port `1852`. You can change this in `appsettings.json`:
    ```json
    {
      // ...
      "Kestrel": {
        "Endpoints": {
          "Http": {
            "Url": "http://*:1852" // Change 1852 to your desired port
          }
        }
      }
      // ...
    }
    ```
4.  **Run the Application:**
    ```bash
    dotnet run
    ```
    The proxy will start, typically listening on `http://localhost:1852`.

### Initial Configuration (Admin UI)

1.  **Access the Admin UI:** Open your web browser and navigate to `http://localhost:1852/admin`.
2.  **Default Configuration:** If this is the first run or `config/dynamic_routing.json` is empty/missing, some default debug configurations will be loaded and saved. You can modify or delete these.
3.  **Add a Model Configuration:**
    *   Enter a **Model Name** (e.g., `gpt-4o`, `phi-3-mini`). This is the identifier clients will use in their requests.
    *   Click "Add Model Section".
4.  **Configure Backends for the Model:**
    *   Select a **Routing Strategy** (Failover, Round Robin, Weighted).
    *   Click "Add Backend".
    *   Fill in the backend details:
        *   **Backend Name:** A descriptive name (e.g., `OpenAI-Primary`, `OpenRouter-Account1`, `LMStudio-Local`).
        *   **Base URL:** The base URL of the backend LLM provider's API.
            *   Example OpenAI: `https://api.openai.com/v1`
            *   Example OpenRouter: `https://openrouter.ai/api/v1`
            *   Example Google Gemini (OpenAI Compatible Layer): `https://generativelanguage.googleapis.com/v1beta/openai`
            *   Example LM Studio: `http://<your-lm-studio-ip>:1234/v1`
        *   **API Keys (comma-separated):** Enter one or more API keys for this backend. If multiple keys are provided, they will be used in a round-robin fashion for this specific backend entry.
        *   **Backend-Specific Model Name (Optional):** If this backend requires a different model identifier than the general "Model Name" you defined, enter it here. Otherwise, leave blank.
        *   **Weight:** (Used for Weighted strategy) A positive integer.
        *   **Enabled:** Check to enable this backend.
    *   Add more backends as needed for failover or distribution.
5.  **Save Changes:** Click the "Save Changes for [Model Name]" button.
6.  Your configuration is saved to `config/dynamic_routing.json` in the application's execution directory.

## 📖 API Endpoints

### Proxy Endpoints (Client-Facing)

These endpoints mimic the OpenAI API structure.

*   **`POST /v1/chat/completions`**
    *   Proxies requests to the chat completions endpoint of a configured backend.
    *   Supports `stream=true`.
*   **`POST /v1/completions`**
    *   Proxies requests to the legacy completions endpoint.
    *   Supports `stream=true`.
*   **`POST /v1/embeddings`**
    *   Proxies requests to the embeddings endpoint.
*   **`GET /v1/models`**
    *   Returns a list of model IDs that are configured and available through this proxy.
    *   Format:
        ```json
        {
          "object": "list",
          "data": [
            { "id": "configured-model-name-1", "object": "model", "owned_by": "organization_owner" },
            { "id": "configured-model-name-2", "object": "model", "owned_by": "organization_owner" }
          ]
        }
        ```

### Management Endpoints

*   **`GET /health`**
    *   Returns a simple health check status: `{"Status": "Healthy"}`.
*   **`GET /admin`**
    *   Redirects to `/admin.html`, the web UI for managing configurations.
*   **`GET /admin/config`**
    *   **[ADMIN API]** Returns the current full routing configuration as JSON.
*   **`POST /admin/config/{modelName}`**
    *   **[ADMIN API]** Adds or updates the configuration for the specified `{modelName}`. Expects a JSON body representing `ModelRoutingConfig`.
*   **`DELETE /admin/config/{modelName}`**
    *   **[ADMIN API]** Deletes the configuration for the specified `{modelName}`.

**Note:** The `/admin/config/*` API endpoints are currently **not secured**. In a production environment, these should be protected with authentication and authorization.

## 🔧 Configuration Details (`dynamic_routing.json`)

The proxy's routing logic is driven by `config/dynamic_routing.json`. Here's an example structure:

```json
{
  "Models": {
    "general-model-name-for-client": { // e.g., "gpt-4o" or "phi-3-mini"
      "Strategy": "Failover", // Or "RoundRobin", "Weighted" (as string)
      "Backends": [
        {
          "Name": "Backend-Provider-1",
          "BaseUrl": "https://api.provider1.com/v1",
          "ApiKeys": ["key1_for_provider1", "key2_for_provider1"],
          "BackendModelName": "provider1-specific-model-name", // Optional
          "Weight": 1,
          "Enabled": true
        },
        {
          "Name": "Backend-Provider-2-Backup",
          "BaseUrl": "https://api.provider2.com/v1beta",
          "ApiKeys": ["key_for_provider2"],
          "BackendModelName": null, // Uses "general-model-name-for-client"
          "Weight": 1,
          "Enabled": true
        }
      ]
    }
    // ... more model configurations
  }
}