#!/usr/bin/env bash
set -euo pipefail

# Integration test for the Restate .NET SDK samples.
# Prerequisites: docker, dotnet SDK, curl
# Usage: ./test/integration-test.sh

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
PIDS=()

cleanup() {
    echo "Cleaning up..."
    for pid in "${PIDS[@]+"${PIDS[@]}"}"; do
        kill "$pid" 2>/dev/null || true
    done
    docker compose -f "$ROOT_DIR/docker-compose.yml" down --volumes 2>/dev/null || true
    echo "Done."
}
trap cleanup EXIT

fail() {
    echo "FAIL: $1" >&2
    exit 1
}

pass() {
    echo "PASS: $1"
}

wait_for_port() {
    local port=$1 retries=30
    for ((i=1; i<=retries; i++)); do
        # Services use HTTP/2 only — use --http2-prior-knowledge for h2c
        if curl -sf --http2-prior-knowledge "http://localhost:$port/discover" >/dev/null 2>&1; then
            return 0
        fi
        sleep 1
    done
    fail "Port $port not ready after $retries seconds"
}

wait_for_restate() {
    local retries=30
    for ((i=1; i<=retries; i++)); do
        if curl -sf "http://localhost:9070/health" >/dev/null 2>&1; then
            return 0
        fi
        sleep 1
    done
    fail "Restate admin not ready after $retries seconds"
}

register_deployment() {
    local port=$1
    local response
    response=$(curl -sf -X POST "http://localhost:9070/deployments" \
        -H "content-type: application/json" \
        -d "{\"uri\": \"http://host.docker.internal:$port\"}" 2>&1) || \
    response=$(curl -sf -X POST "http://localhost:9070/deployments" \
        -H "content-type: application/json" \
        -d "{\"uri\": \"http://localhost:$port\"}" 2>&1) || \
        fail "Failed to register deployment on port $port"
    echo "Registered deployment on port $port"
}

assert_response() {
    local description=$1 url=$2 payload=$3 expected=$4
    local response
    response=$(curl -sf -X POST "$url" \
        -H "content-type: application/json" \
        -d "$payload" 2>&1) || fail "$description: curl failed"

    if echo "$response" | grep -qF "$expected"; then
        pass "$description"
    else
        fail "$description: expected '$expected', got '$response'"
    fi
}

# For void-input handlers: send no body and no content-type.
# The discovery manifest declares input: {} for void handlers, meaning
# "only empty body accepted" — sending content-type: application/json
# with a body (even "null") would be rejected by the Restate runtime.
assert_void_response() {
    local description=$1 url=$2 expected=$3
    local response
    response=$(curl -sf -X POST "$url" 2>&1) || fail "$description: curl failed"

    if echo "$response" | grep -qF "$expected"; then
        pass "$description"
    else
        fail "$description: expected '$expected', got '$response'"
    fi
}

assert_status() {
    local description=$1 url=$2 payload=$3 expected_status=$4
    local http_code
    http_code=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$url" \
        -H "content-type: application/json" \
        -d "$payload" 2>&1) || true

    if [ "$http_code" = "$expected_status" ]; then
        pass "$description"
    else
        fail "$description: expected HTTP $expected_status, got HTTP $http_code"
    fi
}

# For void-input handlers that should return a specific HTTP status
assert_void_status() {
    local description=$1 url=$2 expected_status=$3
    local http_code
    http_code=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$url" 2>&1) || true

    if [ "$http_code" = "$expected_status" ]; then
        pass "$description"
    else
        fail "$description: expected HTTP $expected_status, got HTTP $http_code"
    fi
}

echo "=== Restate .NET SDK Integration Tests ==="
echo ""

# 1. Start Restate server
echo "Starting Restate server..."
docker compose -f "$ROOT_DIR/docker-compose.yml" up -d
wait_for_restate
echo "Restate server ready."
echo ""

# 2. Build samples
echo "Building samples..."
dotnet build "$ROOT_DIR/Restate.Sdk.slnx" -c Release --verbosity quiet || fail "Build failed"
echo "Build complete."
echo ""

# 3. Start sample apps
echo "Starting Greeter on port 9080..."
dotnet run --project "$ROOT_DIR/samples/Greeter" -c Release --no-build &
PIDS+=($!)

echo "Starting Counter on port 9081..."
dotnet run --project "$ROOT_DIR/samples/Counter" -c Release --no-build &
PIDS+=($!)

echo "Starting TicketReservation on port 9082..."
dotnet run --project "$ROOT_DIR/samples/TicketReservation" -c Release --no-build &
PIDS+=($!)

echo "Starting SignupWorkflow on port 9084..."
dotnet run --project "$ROOT_DIR/samples/SignupWorkflow" -c Release --no-build &
PIDS+=($!)

echo "Waiting for services to start..."
wait_for_port 9080
wait_for_port 9081
wait_for_port 9082
wait_for_port 9084
echo "All services ready."
echo ""

# 4. Register deployments
echo "Registering deployments..."
register_deployment 9080
register_deployment 9081
register_deployment 9082
register_deployment 9084
echo ""

# Give Restate a moment to discover handlers
sleep 2

# 5. Run tests
# NOTE: JSON uses camelCase — the source generator configures JsonNamingPolicy.CamelCase
echo "=== Running Tests ==="
echo ""

# --- Greeter (stateless service: ctx.Run + ctx.Sleep) ---

assert_response \
    "GreeterService/Greet" \
    "http://localhost:8080/GreeterService/Greet" \
    '{"name":"World"}' \
    "Hello, World! Welcome aboard."

assert_response \
    "GreeterService/GreetWithCancellation" \
    "http://localhost:8080/GreeterService/GreetWithCancellation" \
    '{"name":"Alice"}' \
    "Hello, Alice!"

# --- Counter (virtual object: state management) ---

assert_response \
    "CounterObject/Add (first)" \
    "http://localhost:8080/CounterObject/my-counter/Add" \
    "5" \
    "5"

assert_response \
    "CounterObject/Add (second)" \
    "http://localhost:8080/CounterObject/my-counter/Add" \
    "3" \
    "8"

assert_void_response \
    "CounterObject/Get (shared)" \
    "http://localhost:8080/CounterObject/my-counter/Get" \
    "8"

assert_void_response \
    "CounterObject/GetKeys (shared)" \
    "http://localhost:8080/CounterObject/my-counter/GetKeys" \
    "count"

assert_void_status \
    "CounterObject/AddThenFail (TerminalException 400)" \
    "http://localhost:8080/CounterObject/my-counter/AddThenFail" \
    "400"

# --- Ticket Reservation (state machine + delayed sends) ---

# TicketState enum: Available=0, Reserved=1, Sold=2 (serialized as int with camelCase keys)
assert_response \
    "TicketObject/Reserve" \
    "http://localhost:8080/TicketObject/ticket-1/Reserve" \
    '{"userId":"alice"}' \
    '"state":1'

assert_void_response \
    "TicketObject/GetStatus (shared)" \
    "http://localhost:8080/TicketObject/ticket-1/GetStatus" \
    '"state":1'

assert_void_response \
    "TicketObject/Confirm" \
    "http://localhost:8080/TicketObject/ticket-1/Confirm" \
    '"state":2'

assert_status \
    "TicketObject/Reserve already-sold (TerminalException 409)" \
    "http://localhost:8080/TicketObject/ticket-1/Reserve" \
    '{"userId":"bob"}' \
    "409"

# Reserve + Cancel flow
assert_response \
    "TicketObject/Reserve (ticket-2)" \
    "http://localhost:8080/TicketObject/ticket-2/Reserve" \
    '{"userId":"charlie"}' \
    '"state":1'

# Cancel returns void — just assert success (curl -sf will fail on non-2xx)
assert_void_response \
    "TicketObject/Cancel (ticket-2)" \
    "http://localhost:8080/TicketObject/ticket-2/Cancel" \
    ""

assert_void_response \
    "TicketObject/GetStatus (after cancel)" \
    "http://localhost:8080/TicketObject/ticket-2/GetStatus" \
    '"state":0'

# --- Signup Workflow (workflow + promises + awakeables) ---

# Send the workflow — it will block on the awakeable, so use the /send endpoint (returns 202)
assert_status \
    "SignupWorkflow/Run (send workflow)" \
    "http://localhost:8080/SignupWorkflow/test-user/Run/send" \
    '{"email":"test@example.com","name":"Test User"}' \
    "202"

# Give the workflow time to start and reach the awakeable
sleep 2

assert_void_response \
    "SignupWorkflow/GetStatus (query)" \
    "http://localhost:8080/SignupWorkflow/test-user/GetStatus" \
    "awaiting-verification"

echo ""
echo "=== All Tests Passed ==="
