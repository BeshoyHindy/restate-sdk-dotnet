#!/usr/bin/env bash
set -euo pipefail

# Integration test for the Restate .NET SDK samples.
# Designed for GitHub Actions CI — uses host networking so Restate
# can reach sample services at localhost.
# Prerequisites: docker, dotnet SDK, curl
# Usage: .github/scripts/integration-test.sh

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
PIDS=()
AOT_DIR=""
RESTATE_IMAGE="docker.io/restatedev/restate:latest"
RESTATE_CONTAINER="restate-ci"

cleanup() {
    echo "Cleaning up..."
    for pid in "${PIDS[@]+"${PIDS[@]}"}"; do
        kill "$pid" 2>/dev/null || true
    done
    if [ -n "$AOT_DIR" ] && [ -d "$AOT_DIR" ]; then
        rm -rf "$AOT_DIR"
    fi
    docker rm -f "$RESTATE_CONTAINER" 2>/dev/null || true
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
    local retries=60
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

# 1. Start Restate server with host networking (CI needs localhost access)
echo "Starting Restate server..."
docker run -d --name "$RESTATE_CONTAINER" --network host "$RESTATE_IMAGE"
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
echo "=== Regular Tests Passed ==="
echo ""

# === Phase 2: NativeAOT Samples ===
echo "=== NativeAOT Integration Tests ==="
echo ""

# Stop regular sample services (AOT samples reuse the same service names)
echo "Stopping regular sample services..."
for pid in "${PIDS[@]+"${PIDS[@]}"}"; do
    kill "$pid" 2>/dev/null || true
done
PIDS=()

# Restart Restate for clean state (AOT services share service names with regular ones)
echo "Restarting Restate server..."
docker rm -f "$RESTATE_CONTAINER"
docker run -d --name "$RESTATE_CONTAINER" --network host "$RESTATE_IMAGE"
wait_for_restate
echo "Restate server ready."
echo ""

# Publish NativeAOT samples
AOT_DIR=$(mktemp -d)
echo "Publishing NativeAOT samples to $AOT_DIR..."
dotnet publish "$ROOT_DIR/samples/NativeAotGreeter" -c Release -o "$AOT_DIR/greeter" --verbosity quiet || fail "AOT publish NativeAotGreeter failed"
echo "  NativeAotGreeter published."
dotnet publish "$ROOT_DIR/samples/NativeAotCounter" -c Release -o "$AOT_DIR/counter" --verbosity quiet || fail "AOT publish NativeAotCounter failed"
echo "  NativeAotCounter published."
dotnet publish "$ROOT_DIR/samples/NativeAotSaga" -c Release -o "$AOT_DIR/saga" --verbosity quiet || fail "AOT publish NativeAotSaga failed"
echo "  NativeAotSaga published."
echo "AOT publish complete."
echo ""

# Start AOT native binaries
echo "Starting NativeAotGreeter on port 9085..."
"$AOT_DIR/greeter/NativeAotGreeter" &
PIDS+=($!)

echo "Starting NativeAotCounter on port 9086..."
"$AOT_DIR/counter/NativeAotCounter" &
PIDS+=($!)

echo "Starting NativeAotSaga on port 9087..."
"$AOT_DIR/saga/NativeAotSaga" &
PIDS+=($!)

echo "Waiting for AOT services to start..."
wait_for_port 9085
wait_for_port 9086
wait_for_port 9087
echo "All AOT services ready."
echo ""

# Register AOT deployments
echo "Registering AOT deployments..."
register_deployment 9085
register_deployment 9086
register_deployment 9087
echo ""

sleep 2

echo "=== Running AOT Tests ==="
echo ""

# --- NativeAotGreeter (Service: ctx.Run) ---

assert_response \
    "AOT GreeterService/Greet" \
    "http://localhost:8080/GreeterService/Greet" \
    '{"name":"Restate"}' \
    "Hello, Restate!"

assert_response \
    "AOT GreeterService/GreetWithRetry" \
    "http://localhost:8080/GreeterService/GreetWithRetry" \
    '{"name":"AOT"}' \
    "Hello, AOT!"

# --- NativeAotCounter (VirtualObject: state management) ---

assert_response \
    "AOT CounterObject/Add (first)" \
    "http://localhost:8080/CounterObject/aot-counter/Add" \
    "10" \
    "10"

assert_response \
    "AOT CounterObject/Add (second)" \
    "http://localhost:8080/CounterObject/aot-counter/Add" \
    "7" \
    "17"

assert_void_response \
    "AOT CounterObject/Reset" \
    "http://localhost:8080/CounterObject/aot-counter/Reset" \
    ""

assert_response \
    "AOT CounterObject/Add (after reset)" \
    "http://localhost:8080/CounterObject/aot-counter/Add" \
    "1" \
    "1"

# --- NativeAotSaga (TripBookingService — compensating transactions) ---
# Car rental has a 20% failure rate with 3 retries (~0.8% chance all fail).
# Both outcomes validate the saga pattern:
#   Success (200): all bookings confirmed
#   Failure (500): TerminalException after compensations ran

SAGA_PAYLOAD='{"tripId":"test-trip","userId":"alice","flight":{"from":"NYC","to":"LAX","date":"2025-06-15"},"hotel":{"city":"Los Angeles","checkIn":"2025-06-15","checkOut":"2025-06-20"},"carRental":{"city":"Los Angeles","pickUp":"2025-06-15","dropOff":"2025-06-20"}}'

saga_response=$(curl -s -w "\n%{http_code}" -X POST "http://localhost:8080/TripBookingService/Book" \
    -H "content-type: application/json" \
    -d "$SAGA_PAYLOAD" 2>&1)
saga_code=$(echo "$saga_response" | tail -1)
saga_body=$(echo "$saga_response" | sed '$d')

if [ "$saga_code" = "200" ]; then
    if echo "$saga_body" | grep -qF "tripId"; then
        pass "AOT TripBookingService/Book (success — all bookings confirmed)"
    else
        fail "AOT TripBookingService/Book: HTTP 200 but missing 'tripId' in: $saga_body"
    fi
elif [ "$saga_code" = "500" ]; then
    pass "AOT TripBookingService/Book (compensated — car rental failed after retries)"
else
    fail "AOT TripBookingService/Book: expected HTTP 200 or 500, got HTTP $saga_code: $saga_body"
fi

echo ""
echo "=== All Tests Passed (Regular + AOT) ==="
