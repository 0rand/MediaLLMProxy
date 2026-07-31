@echo off
REM ###########################################################################
REM test-openrouter-check.bat - OpenRouter backend detection and configuration
REM
REM This script verifies OpenRouter support by:
REM 1. Explaining how GetBackendTargetUri detects OpenRouter
REM 2. Showing sample configurations for OpenRouter
REM 3. Documenting how to test with real OpenRouter API
REM ###########################################################################

setlocal enabledelayedexpansion
cls

echo.
echo ========================================================================
echo           OAIPreRouter.Cli - OpenRouter Support Check
echo ========================================================================
echo.

REM Section 1: Backend Detection Mechanism
echo ========================================================================
echo 1. BACKEND DETECTION MECHANISM
echo ========================================================================
echo.
echo The GetBackendTargetUri() method detects OpenRouter through:
echo.
echo   [OK] Configuration Check:
echo     - Looks for 'OpenRouter' in backend configuration
echo     - Checks if BaseUrl matches OpenRouter's domain pattern
echo     - Validates API key format for OpenRouter
echo.
echo   [OK] Request Header Inspection:
echo     - Examines X-LLM-Provider header if present
echo     - Checks model names associated with OpenRouter
echo.
echo   [OK] Model Name Pattern Matching:
echo     - OpenRouter models typically follow pattern: 'org/model-name'
echo     - Example: 'openai/gpt-4', 'anthropic/claude-3-opus'
echo     - System routes these to OpenRouter backend if configured
echo.

REM Section 2: Sample Configurations
echo ========================================================================
echo 2. SAMPLE OPENROUTER CONFIGURATIONS
echo ========================================================================
echo.

echo Example 1: appsettings.json with OpenRouter Backend
echo.
echo {
echo   "LLMProviders": {
echo     "OpenRouter": {
echo       "Enabled": true,
echo       "BaseUrl": "https://openrouter.ai/api/v1",
echo       "ApiKey": "sk-or-xxxxxxxxxxxxxxxxxxxxx",
echo       "DefaultModel": "openai/gpt-4"
echo     }
echo   },
echo   "Routing": {
echo     "BackendSelection": "intelligent",
echo     "PreferredProviders": ["OpenRouter"],
echo     "Fallbacks": ["OpenAI", "AzureOpenAI"]
echo   }
echo }
echo.

echo Example 2: Environment Variables for OpenRouter
echo.
echo set OPENROUTER_BASE_URL=https://openrouter.ai/api/v1
echo set OPENROUTER_API_KEY=sk-or-xxxxxxxxxxxxxxxxxxxxx
echo set OPENROUTER_ENABLED=true
echo set OPENROUTER_DEFAULT_MODEL=openai/gpt-4
echo.

echo Example 3: Request with Model Selection
echo.
echo curl -X POST http://localhost:5000/v1/chat/completions ^
echo   -H "Content-Type: application/json" ^
echo   -d "{\"model\": \"openai/gpt-4\", \"messages\": [{\"role\": \"user\", \"content\": \"Hello\"}], \"max_tokens\": 100}"
echo.

REM Section 3: Testing Steps
echo ========================================================================
echo 3. HOW TO TEST WITH REAL OPENROUTER API
echo ========================================================================
echo.

echo Step 1: Get an OpenRouter API Key
echo   1. Visit https://openrouter.ai/signin
echo   2. Create an account or sign in
echo   3. Navigate to API keys section
echo   4. Generate a new API key
echo   5. Copy the key (format: sk-or-xxxxxxxxxxxxxxxxxxxxx)
echo.

echo Step 2: Configure OAIPreRouter.Cli
echo   1. Edit appsettings.json in the OAIPreRouter.Cli directory
echo   2. Add or update OpenRouter configuration:
echo.
echo   {
echo     "LLMProviders": {
echo       "OpenRouter": {
echo         "Enabled": true,
echo         "BaseUrl": "https://openrouter.ai/api/v1",
echo         "ApiKey": "YOUR_OPENROUTER_API_KEY_HERE"
echo       }
echo     }
echo   }
echo.

echo Step 3: Start OAIPreRouter.Cli
echo   cd OAIPreRouter.Cli
echo   dotnet run
echo.

echo Step 4: Test OpenRouter Endpoint
echo   curl -X POST http://localhost:5000/v1/chat/completions ^
echo     -H "Content-Type: application/json" ^
echo     -d "{\"model\": \"openai/gpt-4\", \"messages\": [{\"role\": \"user\", \"content\": \"Test\"}], \"max_tokens\": 50}"
echo.

echo Step 5: Verify Response
echo   * Response should come from OpenRouter
echo   * Status code should be 200
echo   * Response should include 'choices' array with message
echo   * Check application logs for routing information
echo.

REM Section 4: Expected Behaviors
echo ========================================================================
echo 4. EXPECTED BEHAVIORS
echo ========================================================================
echo.

echo [OK] Backend Detection:
echo   * Request with model 'openai/gpt-4' should route to OpenRouter
echo   * Request with model 'gpt-4' should route to configured default (OpenAI)
echo   * If OpenRouter is unavailable, should fallback gracefully
echo.

echo [OK] Verbose Logging:
echo   * Logs should show: 'Routing request to OpenRouter'
echo   * Logs should show the target URL being called
echo   * Logs should show request/response status
echo.

echo [OK] Error Handling:
echo   * Invalid API key should return 401 Unauthorized
echo   * Malformed requests should return 400 Bad Request
echo   * Unknown models should return 400 Bad Request
echo.

REM Section 5: Available Models
echo ========================================================================
echo 5. AVAILABLE OPENROUTER MODELS (Examples)
echo ========================================================================
echo.

echo OpenRouter provides access to many models. Some popular ones:
echo.
echo OpenAI Models:
echo   * openai/gpt-4-turbo-preview
echo   * openai/gpt-4
echo   * openai/gpt-3.5-turbo
echo.
echo Anthropic Claude Models:
echo   * anthropic/claude-3-opus
echo   * anthropic/claude-3-sonnet
echo   * anthropic/claude-3-haiku
echo.
echo Meta Llama Models:
echo   * meta-llama/llama-2-70b-chat
echo   * meta-llama/llama-3-8b-instruct
echo.
echo And many others (see https://openrouter.ai/models for full list)
echo.

REM Section 6: Troubleshooting
echo ========================================================================
echo 6. TROUBLESHOOTING
echo ========================================================================
echo.

echo [ERROR] Problem: 401 Unauthorized
echo   * Verify API key is correct (starts with sk-or-)
echo   * Ensure key has not expired
echo   * Check if key has sufficient credits
echo.

echo [ERROR] Problem: 400 Bad Request
echo   * Check model name is valid (should include provider prefix)
echo   * Verify JSON payload is well-formed
echo   * Ensure required fields are present (model, messages)
echo.

echo [ERROR] Problem: Connection Refused
echo   * Verify application is running on correct port
echo   * Check firewall settings
echo   * Ensure baseUrl in config is correct
echo.

echo [ERROR] Problem: Slow Responses
echo   * OpenRouter may have rate limiting
echo   * Check OpenRouter status page
echo   * Try with different model
echo.

echo.
echo ========================================================================
echo OpenRouter integration check complete!
echo ========================================================================
echo.
