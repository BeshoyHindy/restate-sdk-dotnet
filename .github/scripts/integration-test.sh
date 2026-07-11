#!/usr/bin/env bash
set -euo pipefail

# Integration test for the Restate .NET SDK samples.
# Runs on GitHub Actions CI and locally (Linux or Docker Desktop) — uses bridge
# networking with published ports; Restate reaches the sample services on the
# host via host.docker.internal (mapped with --add-host=...:host-gateway).
# Prerequisites: docker, dotnet SDK, curl, openssl
# Usage: .github/scripts/integration-test.sh

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
PIDS=()
AOT_DIR=""
KEY_DIR=""
LOG_DIR=""
RESTATE_IMAGE="docker.io/restatedev/restate:1.7"
RESTATE_CONTAINER="restate-ci"
SUSPEND_CONTAINER="restate-ci-suspend"
IDENTITY_CONTAINER="restate-ci-identity"

cleanup() {
    echo "Cleaning up..."
    for pid in "${PIDS[@]+"${PIDS[@]}"}"; do
        kill "$pid" 2>/dev/null || true
    done
    if [ -n "$AOT_DIR" ] && [ -d "$AOT_DIR" ]; then
        rm -rf "$AOT_DIR"
    fi
    if [ -n "$KEY_DIR" ] && [ -d "$KEY_DIR" ]; then
        rm -rf "$KEY_DIR"
    fi
    if [ -n "$LOG_DIR" ] && [ -d "$LOG_DIR" ]; then
        rm -rf "$LOG_DIR"
    fi
    docker rm -f "$RESTATE_CONTAINER" "$SUSPEND_CONTAINER" "$IDENTITY_CONTAINER" 2>/dev/null || true
    echo "Done."
}
trap cleanup EXIT

# Starts a restate-server container with ingress/admin published on the given
# host ports. Extra docker run args (env vars, volumes) can be appended.
start_restate() {
    local name=$1 ingress_port=$2 admin_port=$3
    shift 3
    docker run -d --name "$name" \
        --add-host=host.docker.internal:host-gateway \
        -p "$ingress_port:8080" -p "$admin_port:9070" \
        "$@" \
        "$RESTATE_IMAGE"
}

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

# Waits until an SDK endpoint answers /discover with a specific HTTP status.
# Used for identity-enforcing services where unsigned requests must get 401,
# so the plain wait_for_port (which requires 2xx) cannot be used.
wait_for_port_status() {
    local port=$1 expected=$2 retries=30
    local code
    for ((i=1; i<=retries; i++)); do
        code=$(curl -s -o /dev/null -w "%{http_code}" --http2-prior-knowledge \
            "http://localhost:$port/discover" 2>/dev/null) || true
        if [ "$code" = "$expected" ]; then
            return 0
        fi
        sleep 1
    done
    fail "Port $port did not return HTTP $expected after $retries seconds (last: $code)"
}

wait_for_restate() {
    local admin_port=${1:-9070} retries=60
    for ((i=1; i<=retries; i++)); do
        if curl -sf "http://localhost:$admin_port/health" >/dev/null 2>&1; then
            return 0
        fi
        sleep 1
    done
    fail "Restate admin on port $admin_port not ready after $retries seconds"
}

register_deployment() {
    local port=$1 admin_port=${2:-9070}
    local response
    # Sample services run on the host; Restate runs in a bridge-networked
    # container and reaches them via host.docker.internal.
    response=$(curl -sf -X POST "http://localhost:$admin_port/deployments" \
        -H "content-type: application/json" \
        -d "{\"uri\": \"http://host.docker.internal:$port\"}" 2>&1) || \
        fail "Failed to register deployment on port $port (admin port $admin_port)"
    echo "Registered deployment on port $port (admin port $admin_port)"
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

# 1. Start Restate server (ingress on 8080, admin on 9070)
echo "Starting Restate server..."
start_restate "$RESTATE_CONTAINER" 8080 9070
wait_for_restate
echo "Restate server ready."
echo ""

# 2. Build samples
echo "Building samples..."
dotnet build "$ROOT_DIR/Restate.Sdk.slnx" -c Release --verbosity quiet || fail "Build failed"
echo "Build complete."
echo ""

# 3. Start sample apps
# Greeter output is captured to a file so the suspension test can assert on
# the SDK's "Invocation suspending" log line.
LOG_DIR=$(mktemp -d)
echo "Starting Greeter on port 9080 (log: $LOG_DIR/greeter.log)..."
dotnet run --project "$ROOT_DIR/samples/Greeter" -c Release --no-build \
    > "$LOG_DIR/greeter.log" 2>&1 &
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

# === Phase 2: Suspension on input EOF ===
# A second restate-server with worker.invoker.inactivity-timeout lowered from
# the 1m default to 500ms: after 500ms without journal traffic the invoker
# closes the request input stream. GreeterService/Greet awaits ctx.Sleep(1s),
# so every invocation is forced through a suspend/resume cycle — the SDK must
# emit a SUSPENSION message at input EOF and complete correctly on the replayed
# re-invocation. A pre-suspension SDK would error or hang here.
echo "=== Suspension Tests ==="
echo ""

echo "Starting Restate (inactivity-timeout=500ms) on ports 18080/19070..."
start_restate "$SUSPEND_CONTAINER" 18080 19070 \
    -e RESTATE_WORKER__INVOKER__INACTIVITY_TIMEOUT=500ms
wait_for_restate 19070
register_deployment 9080 19070
sleep 2

assert_response \
    "GreeterService/Greet completes across suspend/resume (inactivity-timeout < sleep)" \
    "http://localhost:18080/GreeterService/Greet" \
    '{"name":"Suspenders"}' \
    "Hello, Suspenders! Welcome aboard."

# Corroborate that the invocation actually suspended (not merely survived):
# the SDK logs "Invocation suspending" at Information when it replies with a
# SUSPENSION message after input EOF.
sleep 1
if grep -q "Invocation suspending" "$LOG_DIR/greeter.log"; then
    pass "SDK emitted a suspension ('Invocation suspending' logged)"
else
    fail "Greeter log contains no 'Invocation suspending' — the invocation never suspended"
fi

docker rm -f "$SUSPEND_CONTAINER" >/dev/null
echo ""

# NOTE: An ingress awakeable round-trip for SignupWorkflow is intentionally not
# tested here. The sample's EmailService stub discards the awakeable ID (it is
# never logged or returned), so there is no scriptable way to obtain the ID and
# call /restate/awakeables/{id}/resolve without contorting the sample.

# === Phase 3: Request identity verification ===
# A third restate-server is given an Ed25519 request-identity private key
# (request-identity-private-key-pem-file); it then signs every request to the
# SDK with x-restate-jwt-v1. A Greeter instance configured with the matching
# publickeyv1_... key (via WithIdentityKeys) must accept signed requests and
# reject unsigned ones with 401.
echo "=== Request Identity Tests ==="
echo ""

# Find an openssl with Ed25519 support (macOS ships LibreSSL, which lacks it).
OPENSSL_BIN=""
for candidate in openssl /opt/homebrew/bin/openssl /usr/local/bin/openssl; do
    if command -v "$candidate" >/dev/null 2>&1 \
        && "$candidate" genpkey -algorithm ed25519 >/dev/null 2>&1; then
        OPENSSL_BIN=$candidate
        break
    fi
done
[ -n "$OPENSSL_BIN" ] || fail "No openssl with Ed25519 support found"

KEY_DIR=$(mktemp -d)
"$OPENSSL_BIN" genpkey -algorithm ed25519 -outform pem -out "$KEY_DIR/private.pem" \
    || fail "openssl Ed25519 key generation failed"
chmod 644 "$KEY_DIR/private.pem"

echo "Starting Restate (request identity signing) on ports 28080/29070..."
start_restate "$IDENTITY_CONTAINER" 28080 29070 \
    -v "$KEY_DIR:/keys:ro" \
    -e RESTATE_REQUEST_IDENTITY_PRIVATE_KEY_PEM_FILE=/keys/private.pem
wait_for_restate 29070

# restate-server logs the derived public key at startup:
#   Loaded request identity key ... kid: "publickeyv1_..."
PUBLIC_KEY=""
for ((i=1; i<=10; i++)); do
    PUBLIC_KEY=$(docker logs "$IDENTITY_CONTAINER" 2>&1 \
        | grep -o 'publickeyv1_[1-9A-HJ-NP-Za-km-z]*' | head -1) || true
    [ -n "$PUBLIC_KEY" ] && break
    sleep 1
done
[ -n "$PUBLIC_KEY" ] || fail "Could not extract publickeyv1_ key from $IDENTITY_CONTAINER logs"
echo "Server request identity public key: $PUBLIC_KEY"

echo "Starting identity-enforcing Greeter on port 9086..."
RESTATE_IDENTITY_KEYS="$PUBLIC_KEY" GREETER_PORT=9086 \
    dotnet run --project "$ROOT_DIR/samples/Greeter" -c Release --no-build &
PIDS+=($!)

# Readiness probe: unsigned /discover must return 401 once the endpoint is up.
wait_for_port_status 9086 401
pass "Direct unsigned /discover rejected with 401"

# Registration only succeeds if the server-signed discovery request passes
# the SDK's signature verification.
register_deployment 9086 29070
sleep 2

assert_response \
    "GreeterService/Greet via signing server (signed request accepted)" \
    "http://localhost:28080/GreeterService/Greet" \
    '{"name":"Verified"}' \
    "Hello, Verified! Welcome aboard."

docker rm -f "$IDENTITY_CONTAINER" >/dev/null
echo ""
echo "=== Suspension + Identity Tests Passed ==="
echo ""

# === Phase 4: NativeAOT Samples ===
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
start_restate "$RESTATE_CONTAINER" 8080 9070
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

echo "Starting NativeAotCounter on port 9088..."
"$AOT_DIR/counter/NativeAotCounter" &
PIDS+=($!)

echo "Starting NativeAotSaga on port 9089..."
"$AOT_DIR/saga/NativeAotSaga" &
PIDS+=($!)

echo "Waiting for AOT services to start..."
wait_for_port 9085
wait_for_port 9088
wait_for_port 9089
echo "All AOT services ready."
echo ""

# Register AOT deployments
echo "Registering AOT deployments..."
register_deployment 9085
register_deployment 9088
register_deployment 9089
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
echo "=== All Tests Passed (Regular + Suspension + Identity + AOT) ==="
