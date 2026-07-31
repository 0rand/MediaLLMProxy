#!/bin/bash

###############################################################################
# test-verbose-logging.sh - Comprehensive verbose request logging test
# 
# This script tests Phase 8 verbose request logging by:
# 1. Enabling VerboseRequests in appsettings.json
# 2. Starting OAIPreRouter.Cli in background
# 3. Waiting for /health endpoint
# 4. Sending test requests to various endpoints
# 5. Capturing verbose console output
# 6. Cleaning up
###############################################################################

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR"
APP_EXECUTABLE="$PROJECT_DIR/bin/Debug/net8.0/OAIPreRouter.Cli"
APPSETTINGS="$PROJECT_DIR/appsettings.json"
BACKUP_APPSETTINGS="${APPSETTINGS}.backup"
PID_FILE="/tmp/oai_preloader.pid"
LOG_FILE="${SCRIPT_DIR}/test-verbose-logging.log"

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Cleanup on exit
cleanup() {
    echo -e "${YELLOW}[CLEANUP]${NC} Terminating background process..."
    if [ -f "$PID_FILE" ]; then
        PID=$(cat "$PID_FILE")
        if kill -0 "$PID" 2>/dev/null; then
            kill "$PID" 2>/dev/null || true
            sleep 2
            kill -9 "$PID" 2>/dev/null || true
        fi
        rm -f "$PID_FILE"
    fi
    
    # Restore original appsettings.json
    if [ -f "$BACKUP_APPSETTINGS" ]; then
        echo -e "${YELLOW}[CLEANUP]${NC} Restoring original appsettings.json..."
        mv "$BACKUP_APPSETTINGS" "$APPSETTINGS"
    fi
    
    echo -e "${GREEN}[DONE]${NC} Cleanup complete."
}

trap cleanup EXIT

echo -e "${BLUE}╔════════════════════════════════════════════════════════════════════╗${NC}"
echo -e "${BLUE}║         OAIPreRouter.Cli - Verbose Request Logging Test           ║${NC}"
echo -e "${BLUE}╚════════════════════════════════════════════════════════════════════╝${NC}"
echo ""

# Step 1: Build the project
echo -e "${YELLOW}[1/7]${NC} Building OAIPreRouter.Cli..."
cd "$PROJECT_DIR"
dotnet build -c Debug --no-restore > /dev/null 2>&1 || {
    echo -e "${RED}[ERROR]${NC} Build failed. Please check the project."
    exit 1
}
echo -e "${GREEN}[✓]${NC} Build successful"

# Step 2: Backup and modify appsettings.json
echo -e "${YELLOW}[2/7]${NC} Configuring appsettings.json..."
cp "$APPSETTINGS" "$BACKUP_APPSETTINGS"

# Enable VerboseRequests using jq or sed
if command -v jq &> /dev/null; then
    jq '.Logging.VerboseRequests = true' "$APPSETTINGS" > "${APPSETTINGS}.tmp"
    mv "${APPSETTINGS}.tmp" "$APPSETTINGS"
else
    # Fallback: sed-based replacement
    sed -i 's/"VerboseRequests": false/"VerboseRequests": true/g' "$APPSETTINGS" || true
fi
echo -e "${GREEN}[✓]${NC} VerboseRequests enabled"

# Step 3: Start the background process
echo -e "${YELLOW}[3/7]${NC} Starting OAIPreRouter.Cli in background..."
cd "$PROJECT_DIR"
dotnet run --no-build > "$LOG_FILE" 2>&1 &
APP_PID=$!
echo $APP_PID > "$PID_FILE"
sleep 3
echo -e "${GREEN}[✓]${NC} Application started (PID: $APP_PID)"

# Step 4: Wait for /health endpoint
echo -e "${YELLOW}[4/7]${NC} Waiting for application to be ready (checking /health)..."
HEALTH_READY=0
for i in {1..30}; do
    if curl -s http://localhost:5000/health > /dev/null 2>&1; then
        HEALTH_READY=1
        break
    fi
    echo -n "."
    sleep 1
done

if [ $HEALTH_READY -eq 0 ]; then
    echo -e "${RED}[ERROR]${NC} Application failed to start. Check logs:"
    tail -20 "$LOG_FILE"
    exit 1
fi
echo -e "${GREEN}[✓]${NC} Application is ready"

# Step 5: Send test requests
echo -e "${YELLOW}[5/7]${NC} Sending test requests..."
echo ""

# Test 1: POST to /v1/chat/completions
echo -e "${BLUE}  • Testing /v1/chat/completions endpoint...${NC}"
curl -s -X POST http://localhost:5000/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "gpt-4",
    "messages": [{"role": "user", "content": "Hello"}],
    "max_tokens": 100
  }' > /dev/null
echo -e "${GREEN}    ✓ Request sent${NC}"

sleep 1

# Test 2: POST to /api/chat
echo -e "${BLUE}  • Testing /api/chat endpoint...${NC}"
curl -s -X POST http://localhost:5000/api/chat \
  -H "Content-Type: application/json" \
  -d '{
    "model": "gpt-3.5-turbo",
    "messages": [{"role": "user", "content": "Test message"}],
    "temperature": 0.7
  }' > /dev/null
echo -e "${GREEN}    ✓ Request sent${NC}"

sleep 1

# Test 3: GET to unknown endpoint (fallback)
echo -e "${BLUE}  • Testing /unknown endpoint (fallback handler)...${NC}"
curl -s http://localhost:5000/unknown > /dev/null
echo -e "${GREEN}    ✓ Request sent${NC}"

echo ""
echo -e "${GREEN}[✓]${NC} All test requests sent"

# Step 6: Collect verbose logs
echo -e "${YELLOW}[6/7]${NC} Collecting verbose logs..."
sleep 2

echo ""
echo -e "${BLUE}════════════════════════════════════════════════════════════════════${NC}"
echo -e "${BLUE}VERBOSE REQUEST LOGS${NC}"
echo -e "${BLUE}════════════════════════════════════════════════════════════════════${NC}"
echo ""

if [ -f "$LOG_FILE" ]; then
    cat "$LOG_FILE"
else
    echo "No log file found."
fi

# Step 7: Summary
echo ""
echo -e "${BLUE}════════════════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN}TEST SUMMARY${NC}"
echo -e "${BLUE}════════════════════════════════════════════════════════════════════${NC}"
echo ""
echo -e "${GREEN}✓ Verbose logging enabled${NC}"
echo -e "${GREEN}✓ Application started successfully${NC}"
echo -e "${GREEN}✓ Sent 3 test requests (POST /v1/chat/completions, POST /api/chat, GET /unknown)${NC}"
echo -e "${GREEN}✓ Console output captured and displayed above${NC}"
echo ""
echo "What to verify in the logs:"
echo "  • VerboseRequests setting is enabled"
echo "  • Request logging shows endpoint paths"
echo "  • Request logging shows method types (GET, POST)"
echo "  • Request logging shows response status codes"
echo "  • Fallback handler logs unknown endpoints"
echo ""
echo -e "${BLUE}════════════════════════════════════════════════════════════════════${NC}"
