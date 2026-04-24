#!/usr/bin/env python3
"""tools/quoter-smoke.py -- deterministic smoke harness for the quoter service.

Usage:
    python3 tools/quoter-smoke.py --uat 1    # automate HUMAN-UAT item 1 (MC regime-force end-to-end)
    python3 tools/quoter-smoke.py --uat 2    # automate HUMAN-UAT item 2 (docker compose up healthy)
    python3 tools/quoter-smoke.py --uat 3    # semi-automated HUMAN-UAT item 3 (~12 min memory + replace rate)
    python3 tools/quoter-smoke.py --uat all  # items 1 + 2 automated, then item 3 data-capture

Constraints:
- Python 3.12+ stdlib only (no pip install on host).
- Uses `docker compose exec -T rabbitmq rabbitmqadmin ...` for broker I/O
  (rabbitmqadmin ships inside rabbitmq:4-management at /usr/local/bin/).

  The rabbitmq:4-management image carries rabbitmqadmin-ng (Rust rewrite,
  v2.29+) whose CLI differs from the classic Python admin. New syntax:
    - publish:  `publish message -e <exch> -k <key> -m <body>`
    - declare:  `declare queue --name X --durable false --auto-delete true`
                `declare binding --source X --destination-type queue
                                 --destination Y --routing-key Z`
    - get:      `get messages -q <queue> -c <count> -a <ackmode>`
  The top-level `--non-interactive` flag suppresses prompts; `-q` (quiet)
  suppresses table headers so `get messages` emits one whitespace-separated
  line per message (empty stdout when the queue is empty). The two flags
  are mutually exclusive with `--table-style`, so we pass them together
  and rely on line counting to answer the only question the harness asks:
  "did any message arrive?"

- Exit 0 = pass; non-zero = fail. Stdout is the verdict.
- /tmp/quoter-smoke-stats.jsonl captures docker stats snapshots for --uat 3.

Exit codes:
  0 = all requested UAT items passed
  1 = container failed to become Healthy within timeout
  2 = RegimeChange envelope not observed within 2s of MC force publish
  3 = quoter log contains scenario-path startup error OR expected startup log line missing
  6 = accumulated ReplaceOrder count was zero during --uat 3 (regression indicator)
"""

import argparse
import json
import subprocess
import sys
import time
import uuid
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
STATS_LOG = Path("/tmp/quoter-smoke-stats.jsonl")

# Regime enum integer values mirror src/quoter/Schedule/Scenario.cs:46-53.
# System.Text.Json on the consumer side has no JsonStringEnumConverter; the
# wire format is therefore the raw integer (Calm=1, Trending=2, Volatile=3,
# Shock=4). Unspecified=0 is intentionally excluded -- publishing it would
# be a no-op on the schedule but is not a legal human-facing regime value.
REGIME_VALUES = {
    "Calm": 1,
    "Trending": 2,
    "Volatile": 3,
    "Shock": 4,
}


def _run(cmd, *, check=True, capture=True, text=True):
    """Thin subprocess wrapper -- inherits host env; repo-root cwd.

    Always called with a list arg-vector (never shell=True) so no user-facing
    string is ever handed to a shell. argparse `choices` gates the --uat value
    upstream; the REGIME_VALUES dict gates regime names in publish helpers.
    """
    return subprocess.run(
        cmd, cwd=REPO_ROOT, check=check, capture_output=capture, text=text
    )


def log(msg):
    print(f"[quoter-smoke] {msg}", flush=True)


# ---------- docker compose primitives ----------

def compose_up(services):
    log(f"docker compose up -d --wait --wait-timeout 60 {' '.join(services)}")
    _run([
        "docker", "compose", "up", "-d",
        "--wait", "--wait-timeout", "60",
        *services,
    ])


def compose_down():
    log("docker compose down --timeout 10")
    _run(["docker", "compose", "down", "--timeout", "10"], check=False)


def compose_exec_rabbit(args):
    """Run a rabbitmqadmin command inside the rabbitmq container via compose.

    Always prepends `--non-interactive` to suppress confirmation prompts; this
    is required for scripted use of rabbitmqadmin-ng.
    """
    return _run(
        [
            "docker", "compose", "exec", "-T", "rabbitmq",
            "rabbitmqadmin", "--non-interactive", *args,
        ],
        check=False,
    )


def compose_exec_rabbit_quiet(args):
    """Like compose_exec_rabbit but also passes -q (quiet) to suppress table
    headers. Used for `get messages` where one-line-per-message output is
    desired for simple line-count parsing.
    """
    return _run(
        [
            "docker", "compose", "exec", "-T", "rabbitmq",
            "rabbitmqadmin", "--non-interactive", "-q", *args,
        ],
        check=False,
    )


def quoter_healthy():
    result = _run(
        ["docker", "compose", "ps", "--format", "json"], check=False
    )
    if result.returncode != 0:
        return False
    # docker compose ps --format json emits one JSON object per line (not an array).
    for line in result.stdout.splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        if obj.get("Service") == "quoter":
            return obj.get("Health") == "healthy"
    return False


def quoter_container_id():
    result = _run(["docker", "compose", "ps", "-q", "quoter"], check=False)
    return result.stdout.strip() or None


# ---------- rabbitmq tap-queue + publish ----------

def declare_exchange(exchange, exchange_type="topic", durable=True):
    """Declare a RabbitMQ exchange idempotently.

    Required because the harness only starts `rabbitmq + quoter` -- the
    exchange service (which owns RabbitMqTopology.DeclareExchangeTopologyAsync)
    is not running, and the quoter publishes to bifrost.public /
    bifrost.cmd / bifrost.private via BasicPublishAsync without redeclaring.
    Without a pre-declared exchange, the tap-queue binding step below returns
    API 404 Not Found.
    """
    compose_exec_rabbit([
        "declare", "exchange",
        "--name", exchange,
        "--type", exchange_type,
        "--durable", "true" if durable else "false",
    ])


def declare_tap_queue(queue, exchange, routing_key):
    """Create an ephemeral tap queue bound to `exchange` with `routing_key`.

    The tap queue lets the harness observe published messages without
    competing with real consumers. --auto-delete=true so the queue disappears
    when the broker restarts or the last consumer goes away. The `exchange`
    is declared first so the binding step does not 404 in deployments where
    the exchange service is not yet running.
    """
    declare_exchange(exchange, exchange_type="topic", durable=True)
    compose_exec_rabbit([
        "declare", "queue",
        "--name", queue,
        "--durable", "false",
        "--auto-delete", "true",
    ])
    compose_exec_rabbit([
        "declare", "binding",
        "--source", exchange,
        "--destination-type", "queue",
        "--destination", queue,
        "--routing-key", routing_key,
    ])


def tap_queue_get_count(queue, count=100, ackmode="ack_requeue_false"):
    """Poll up to `count` messages from the tap queue; return how many came back.

    Uses rabbitmqadmin-ng `-q` (quiet) mode: one whitespace-separated line per
    message, empty stdout when the queue is empty. Line-counting is the
    simplest reliable signal given the lack of a JSON output flag in v2.x.
    """
    result = compose_exec_rabbit_quiet([
        "get", "messages",
        "-q", queue,
        "-c", str(count),
        "-a", ackmode,
    ])
    if result.returncode != 0:
        return 0
    # Strip trailing newline, then count non-empty lines.
    lines = [ln for ln in result.stdout.splitlines() if ln.strip()]
    return len(lines)


def publish_mc_regime_force(regime, nonce=None):
    """Publish McRegimeForceDto JSON on bifrost.mc / mc.regime.force.

    Wire shape matches src/quoter/Rabbit/McRegimeForceDto.cs -- record with
    camelCase-serialized fields `regime` (integer) and `nonce` (GUID string).
    The consumer's JsonSerializerOptions are PropertyNamingPolicy=CamelCase
    without a JsonStringEnumConverter, so regime MUST be the integer value.
    """
    if regime not in REGIME_VALUES:
        raise ValueError(f"Unknown regime {regime!r}; known: {list(REGIME_VALUES)}")
    if nonce is None:
        nonce = str(uuid.uuid4())
    payload = json.dumps(
        {"regime": REGIME_VALUES[regime], "nonce": nonce},
        separators=(",", ":"),
    )
    result = compose_exec_rabbit([
        "publish", "message",
        "-e", "bifrost.mc",
        "-k", "mc.regime.force",
        "-m", payload,
    ])
    if result.returncode != 0:
        raise RuntimeError(
            f"rabbitmqadmin publish failed (rc={result.returncode}): "
            f"stdout={result.stdout!r} stderr={result.stderr!r}"
        )
    return nonce


# ---------- UAT subcommands ----------

def uat_1_mc_regime_force():
    """Publish McRegimeForceDto; assert RegimeChange envelope on bifrost.public within 2s.

    Observable invariant: the quoter's RegimeSchedule.InstallMcForce path
    produces a RegimeChange event when the forced regime differs from current,
    and RegimeChangePublisher emits it on bifrost.public with routing key
    `events.regime.change`. This test asserts *some* message arrives on the
    tap queue within 2 s -- it does not validate envelope shape.
    """
    log("=== UAT 1: MC regime-force end-to-end ===")
    try:
        compose_up(["rabbitmq", "quoter"])
    except subprocess.CalledProcessError:
        log("ERROR: compose up failed; quoter did not become Healthy within 60s")
        return 1

    # Tap queue on bifrost.public for RegimeChange envelopes.
    tap_queue = "smoke-regime-tap"
    declare_tap_queue(
        tap_queue,
        exchange="bifrost.public",
        routing_key="events.regime.change",
    )
    # Drain anything already in the tap queue from startup warmup.
    tap_queue_get_count(tap_queue, count=100)

    publish_mc_regime_force("Volatile")

    deadline = time.monotonic() + 2.0
    observed = 0
    while time.monotonic() < deadline:
        observed = tap_queue_get_count(tap_queue, count=5)
        if observed > 0:
            break
        time.sleep(0.1)

    if observed == 0:
        log("FAIL: RegimeChange envelope not observed within 2s of MC force publish")
        return 2

    log(
        f"PASS: observed {observed} envelope(s) on bifrost.public "
        f"(events.regime.change)"
    )
    return 0


def uat_2_healthy_boot():
    """docker compose up --wait; assert quoter container Healthy within 60s.

    Also parses `docker compose logs quoter` for the scenario-path startup
    error strings Program.cs:76 would throw if Quoter:ScenarioPath failed to
    resolve, and for the McRegimeForceConsumer startup log line that
    confirms the RabbitMQ connection was established.
    """
    log("=== UAT 2: docker compose up healthy ===")
    try:
        compose_up(["rabbitmq", "quoter"])
    except subprocess.CalledProcessError:
        log("FAIL: compose up failed (quoter did not reach Healthy state)")
        return 1

    if not quoter_healthy():
        log("FAIL: quoter service is not Healthy after compose up --wait")
        return 1

    # Confirm the scenario path resolved (no Quoter:ScenarioPath exception in logs).
    logs = _run(["docker", "compose", "logs", "quoter"], check=False).stdout
    if "ScenarioPath not configured" in logs:
        log("FAIL: quoter log contains 'ScenarioPath not configured' startup error")
        return 3
    if "FileNotFoundException" in logs and "/scenarios/" in logs:
        log("FAIL: quoter log contains FileNotFoundException for /scenarios/ path")
        return 3
    if "MC regime-force consumer started" not in logs:
        log("FAIL: expected 'MC regime-force consumer started' log line not observed")
        return 3

    log("PASS: quoter Healthy with scenario path resolved and consumer started")
    return 0


def uat_3_long_horizon_mem(duration_s=720):
    """Capture docker stats every 10s for duration_s seconds.

    Default duration = 12 minutes (2 min warmup + 10 min sim). Writes
    /tmp/quoter-smoke-stats.jsonl for human review. Flips a regime every
    ~30 s to manufacture fair-value drift and drive the ReplaceOrder path.

    Minimal automated sanity: fails if accumulated ReplaceOrder count on the
    bifrost.cmd tap is zero (regression indicator for the jitter-guard /
    tracker-wiring invariants this phase re-validates).
    """
    log(f"=== UAT 3: long-horizon memory + replace rate (duration={duration_s}s) ===")
    try:
        compose_up(["rabbitmq", "quoter"])
    except subprocess.CalledProcessError:
        log("ERROR: compose up failed")
        return 1

    if STATS_LOG.exists():
        STATS_LOG.unlink()

    # Tap bifrost.cmd for ReplaceOrder counting (human review + exit-code 6 sanity).
    replace_tap = "smoke-replace-tap"
    declare_tap_queue(
        replace_tap,
        exchange="bifrost.cmd",
        routing_key="cmd.order.replace",
    )

    quoter_id = quoter_container_id()
    if quoter_id is None:
        log("ERROR: could not resolve quoter container id")
        return 1

    regime_cycle = ("Calm", "Trending", "Volatile", "Shock")
    loops = duration_s // 10
    replace_count = 0

    for i in range(loops):
        # Capture one docker stats snapshot per 10s tick.
        result = _run(
            ["docker", "stats", quoter_id, "--no-stream", "--format", "{{json .}}"],
            check=False,
        )
        if result.returncode == 0 and result.stdout.strip():
            with STATS_LOG.open("a") as f:
                f.write(result.stdout)

        # Every 3 iterations (~30s), force a regime flip to manufacture fair-value drift.
        if i % 3 == 0:
            regime = regime_cycle[(i // 3) % len(regime_cycle)]
            try:
                publish_mc_regime_force(regime)
            except RuntimeError as e:
                log(f"WARN: regime publish failed at i={i}: {e}")

        # Count replaces accumulated over the last 10s window.
        replace_count += tap_queue_get_count(replace_tap, count=100)

        time.sleep(10)

    log(f"Captured {loops} stats snapshots to {STATS_LOG}")
    log(f"Accumulated ReplaceOrder count on cmd.order.replace tap: {replace_count}")
    log("Human reviewer: inspect /tmp/quoter-smoke-stats.jsonl for non-monotonic MemUsage.")
    log("Human reviewer: confirm replace_count > 0 (jitter-guard / tracker-wiring proof).")

    if replace_count == 0:
        log(
            "FAIL: replace_count == 0 -- regression suspected "
            "(jitter guard suppressing everything or tracker wiring broken)"
        )
        return 6

    log("PASS (data captured for human review; automated minimums met)")
    return 0


# ---------- entrypoint ----------

def main():
    parser = argparse.ArgumentParser(
        prog="quoter-smoke.py",
        description="Deterministic smoke harness for the quoter service.",
    )
    parser.add_argument(
        "--uat",
        required=True,
        choices=["1", "2", "3", "all"],
        help="Which HUMAN-UAT item to run (1, 2, 3) or 'all' to chain them.",
    )
    args = parser.parse_args()

    rc = 0
    try:
        if args.uat in ("1", "all"):
            rc = uat_1_mc_regime_force()
            if rc != 0:
                return rc
        if args.uat in ("2", "all"):
            rc = uat_2_healthy_boot()
            if rc != 0:
                return rc
        if args.uat in ("3", "all"):
            rc = uat_3_long_horizon_mem()
            if rc != 0:
                return rc
        return 0
    finally:
        compose_down()


if __name__ == "__main__":
    sys.exit(main())
