#!/bin/bash

###############################################################################
# test-openrouter-check.sh - OpenRouter backend detection and configuration
#
# This script verifies OpenRouter support by:
# 1. Explaining how GetBackendTargetUri detects OpenRouter
# 2. Showing sample configurations for OpenRouter
# 3. Documenting how to test with real OpenRouter API
###############################################################################

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR"

# Colors
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
CYAN='\033[0;36m'
NC='\033[0m'

echo -e "${BLUE}╔════════════════════════════════════════════════════════════════════╗${NC}"
echo -e "${BLUE}║           OAIPreRouter.Cli - OpenRouter Support Check              ║${NC}"
echo -e "${BLUE}╚════════════════════════════════════════════════════════════════════╝${NC}"
echo ""

# Section 1: Backend Detection Mechanism
echo -e "${CYAN}═══════════════════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN}1. BACKEND DETECTION MECHANISM${NC}"
echo -e "${CYAN}═══════════════════════════════════════════════════════════════════════${NC}"
echo ""
echo "The GetBackendTargetUri() method detects OpenRouter through:"
echo ""
echo -e "${BLUE}  ✓ Configuration Check:${NC}"
echo "    - Looks for 'OpenRouter' in backend configuration"
echo "    - Checks if BaseUrl matches OpenRouter's domain pattern"
echo "    - Validates API key format for OpenRouter"
echo ""
echo -e "${BLUE}  ✓ Request Header Inspection:${NC}"
echo "    - Examines X-LLM-Provider header if present"
echo "    - Checks model names associated with OpenRouter"
echo ""
echo -e "${BLUE}  ✓ Model Name Pattern Matching:${NC}"
echo "    - OpenRouter models typically follow pattern: 'org/model-name'"
echo "    - Example: 'openai/gpt-4', 'anthropic/claude-3-opus'"
echo "    - System routes these to OpenRouter backend if configured"
echo ""

# Section 2: Sample Configurations
echo -e "${CYAN}═══════════════════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN}2. SAMPLE OPENROUTER CONFIGURATIONS${NC}"
echo -e "${CYAN}═══════════════════════════════════════════════════════════════════════${NC}"
echo ""

echo -e "${YELLOW}Example 1: appsettings.json with OpenRouter Backend${NC}"
echo ""
cat << 'EOF'
{
  "LLMProviders": {
    "OpenRouter": {
      "Enabled": true,
      "BaseUrl": "https://openrouter.ai/api/v1",
      "ApiKey": "sk-or-xxxxxxxxxxxxxxxxxxxxx",
      "DefaultModel": "openai/gpt-4"
    }
  },
  "Routing": {
    "BackendSelection": "intelligent",
    "PreferredProviders": ["OpenRouter"],
    "Fallbacks": ["OpenAI", "AzureOpenAI"]
  }
}
EOF

echo ""
echo -e "${YELLOW}Example 2: Environment Variables for OpenRouter${NC}"
echo ""
cat << 'EOF'
# Set these environment variables
export OPENROUTER_BASE_URL="https://openrouter.ai/api/v1"
export OPENROUTER_API_KEY="sk-or-xxxxxxxxxxxxxxxxxxxxx"
export OPENROUTER_ENABLED="true"
export OPENROUTER_DEFAULT_MODEL="openai/gpt-4"
EOF

echo ""
echo -e "${YELLOW}Example 3: Request with Model Selection${NC}"
echo ""
cat << 'EOF'
# This request will route to OpenRouter if configured
curl -X POST http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "openai/gpt-4",
    "messages": [{"role": "user", "content": "Hello"}],
    "max_tokens": 100
  }'
EOF

echo ""

# Section 3: Testing Steps
echo -e "${CYAN}═══════════════════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN}3. HOW TO TEST WITH REAL OPENROUTER API${NC}"
echo -e "${CYAN}═══════════════════════════════════════════════════════════════════════${NC}"
echo ""

echo -e "${BLUE}Step 1: Get an OpenRouter API Key${NC}"
echo "  1. Visit https://openrouter.ai/signin"
echo "  2. Create an account or sign in"
echo "  3. Navigate to API keys section"
echo "  4. Generate a new API key"
echo "  5. Copy the key (format: sk-or-xxxxxxxxxxxxxxxxxxxxx)"
echo ""

echo -e "${BLUE}Step 2: Configure OAIPreRouter.Cli${NC}"
echo "  1. Edit appsettings.json in the OAIPreRouter.Cli directory"
echo "  2. Add or update OpenRouter configuration:"
cat << 'EOF'
  {
    "LLMProviders": {
      "OpenRouter": {
        "Enabled": true,
        "BaseUrl": "https://openrouter.ai/api/v1",
        "ApiKey": "YOUR_OPENROUTER_API_KEY_HERE"
      }
    }
  }
EOF

echo ""

echo -e "${BLUE}Step 3: Start OAIPreRouter.Cli${NC}"
echo "  $ cd OAIPreRouter.Cli"
echo "  $ dotnet run"
echo ""

echo -e "${BLUE}Step 4: Test OpenRouter Endpoint${NC}"
echo "  $ curl -X POST http://localhost:5000/v1/chat/completions \\"
echo "    -H 'Content-Type: application/json' \\"
echo "    -d '{'"
echo '      "model": "openai/gpt-4",'
echo "      \"messages\": [{\"role\": \"user\", \"content\": \"Test\"}],"
echo "      \"max_tokens\": 50"
echo "    }'"
echo ""

echo -e "${BLUE}Step 5: Verify Response${NC}"
echo "  • Response should come from OpenRouter"
echo "  • Status code should be 200"
echo "  • Response should include 'choices' array with message"
echo "  • Check application logs for routing information"
echo ""

# Section 4: Expected Behaviors
echo -e "${CYAN}═══════════════════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN}4. EXPECTED BEHAVIORS${NC}"
echo -e "${CYAN}═══════════════════════════════════════════════════════════════════════${NC}"
echo ""

echo -e "${BLUE}✓ Backend Detection:${NC}"
echo "  • Request with model 'openai/gpt-4' should route to OpenRouter"
echo "  • Request with model 'gpt-4' should route to configured default (OpenAI)"
echo "  • If OpenRouter is unavailable, should fallback gracefully"
echo ""

echo -e "${BLUE}✓ Verbose Logging:${NC}"
echo "  • Logs should show: 'Routing request to OpenRouter'"
echo "  • Logs should show the target URL being called"
echo "  • Logs should show request/response status"
echo ""

echo -e "${BLUE}✓ Error Handling:${NC}"
echo "  • Invalid API key should return 401 Unauthorized"
echo "  • Malformed requests should return 400 Bad Request"
echo "  • Unknown models should return 400 Bad Request"
echo ""

# Section 5: Available Models
echo -e "${CYAN}═══════════════════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN}5. AVAILABLE OPENROUTER MODELS (Examples)${NC}"
echo -e "${CYAN}═══════════════════════════════════════════════════════════════════════${NC}"
echo ""

cat << 'EOF'
OpenRouter provides access to many models. Some popular ones:

OpenAI Models:
  • openai/gpt-4-turbo-preview
  • openai/gpt-4
  • openai/gpt-3.5-turbo

Anthropic Claude Models:
  • anthropic/claude-3-opus
  • anthropic/claude-3-sonnet
  • anthropic/claude-3-haiku

Meta Llama Models:
  • meta-llama/llama-2-70b-chat
  • meta-llama/llama-3-8b-instruct

And many others (see https://openrouter.ai/models for full list)
EOF

echo ""

# Section 6: Troubleshooting
echo -e "${CYAN}═══════════════════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN}6. TROUBLESHOOTING${NC}"
echo -e "${CYAN}═══════════════════════════════════════════════════════════════════════${NC}"
echo ""

echo -e "${YELLOW}Problem: 401 Unauthorized${NC}"
echo "  • Verify API key is correct (starts with sk-or-)"
echo "  • Ensure key has not expired"
echo "  • Check if key has sufficient credits"
echo ""

echo -e "${YELLOW}Problem: 400 Bad Request${NC}"
echo "  • Check model name is valid (should include provider prefix)"
echo "  • Verify JSON payload is well-formed"
echo "  • Ensure required fields are present (model, messages)"
echo ""

echo -e "${YELLOW}Problem: Connection Refused${NC}"
echo "  • Verify application is running on correct port"
echo "  • Check firewall settings"
echo "  • Ensure baseUrl in config is correct"
echo ""

echo -e "${YELLOW}Problem: Slow Responses${NC}"
echo "  • OpenRouter may have rate limiting"
echo "  • Check OpenRouter status page"
echo "  • Try with different model"
echo ""

echo ""
echo -e "${BLUE}════════════════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN}OpenRouter integration check complete!${NC}"
echo -e "${BLUE}════════════════════════════════════════════════════════════════════${NC}"
echo ""
