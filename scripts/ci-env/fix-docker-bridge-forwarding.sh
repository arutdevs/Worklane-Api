#!/usr/bin/env bash
# Align Docker's iptables backend with the legacy filter table that actually drops
# compose-bridge packets on a cloud agent VM.
#
# Symptom (normal bridge, not host network):
#   s3 is healthy, http://127.0.0.1:9000/health is 200, and a TCP connect from the
#   host to the container IPv4 (for example 172.18.0.2:9000) succeeds.
#   A second container on the same bridge times out, and s3-init exits 3 with
#   "HTTP connect" after about 3.1s.
#
# Cause, measured on this kernel with Docker 29 firewall backend "iptables":
#   iptables-legacy filter FORWARD policy is DROP. Its DOCKER-FORWARD / DOCKER-CT /
#   DOCKER-BRIDGE rules name docker0 only, so their ACCEPT counters stay 0.
#   Bridge SYNs (60 bytes) traverse DOCKER-USER, DOCKER-CT, DOCKER-INTERNAL, and
#   DOCKER-BRIDGE, then increment the legacy FORWARD DROP counter.
#   The iptables alternative points at iptables-nft, so dockerd writes the
#   per-bridge ACCEPT rules into nftables. Those nft ACCEPT counters increase
#   for the same SYNs, and the packet is still dropped: br_netfilter
#   (net.bridge.bridge-nf-call-iptables=1) evaluates the legacy table as well.
#   RustFS listening only on :::9000 is not the failure. bindv6only is 0, and
#   the host IPv4 connect to that socket succeeds.
#
# Setting net.bridge.bridge-nf-call-iptables=0 also lets the peer TCP connect,
# and setting it back to 1 brings the timeout back. dockerd turns the sysctl
# back on whenever IPv4 forwarding is enabled, and clearing it disables bridge
# filtering. This script does not change that sysctl.
#
# What it does:
#   When the legacy DOCKER-FORWARD chain exists and FORWARD policy is DROP,
#   point iptables and ip6tables at the legacy binaries. If dockerd is running,
#   restart it so it installs ACCEPT rules for the up bridges in that table.
#   A second run does nothing once those rules are present.
#
# It does not edit compose files, publish ports on the host network, or remove
# volumes.

set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
  exec sudo -E bash "$0" "$@"
fi

if ! command -v iptables-legacy >/dev/null 2>&1 || ! command -v ip6tables-legacy >/dev/null 2>&1; then
  echo "iptables-legacy is not installed; bridge filter alignment skipped."
  exit 0
fi

if ! iptables-legacy -S >/dev/null 2>&1; then
  echo "iptables-legacy -S failed; bridge filter alignment skipped."
  exit 1
fi

if ! iptables-legacy -S | grep -q '^-N DOCKER-FORWARD$'; then
  echo "No iptables-legacy DOCKER-FORWARD chain. Docker is not filtering through the legacy table."
  exit 0
fi

if ! iptables-legacy -S FORWARD | grep -q '^-P FORWARD DROP$'; then
  echo "iptables-legacy FORWARD policy is not DROP."
  exit 0
fi

need_restart=0
if ! iptables --version 2>/dev/null | grep -q '(legacy)'; then
  update-alternatives --set iptables /usr/sbin/iptables-legacy
  update-alternatives --set ip6tables /usr/sbin/ip6tables-legacy
  need_restart=1
  echo "Pointed iptables and ip6tables at the legacy backend ($(iptables --version))."
fi

if [ "$need_restart" -eq 0 ]; then
  while read -r ifname _; do
    [ -n "${ifname:-}" ] || continue
    state="$(cat "/sys/class/net/${ifname}/operstate" 2>/dev/null || echo unknown)"
    [ "$state" = "up" ] || continue
    if ! iptables -S DOCKER-FORWARD | grep -q -F -- "-i ${ifname} "; then
      echo "Up bridge ${ifname} has no legacy DOCKER-FORWARD accept rule."
      need_restart=1
    fi
  done < <(ip -br link show type bridge)
fi

bridge_nf="$(cat /proc/sys/net/bridge/bridge-nf-call-iptables 2>/dev/null || echo missing)"

if [ "$need_restart" -eq 0 ]; then
  echo "iptables legacy backend already has accept rules for up bridges. net.bridge.bridge-nf-call-iptables=${bridge_nf}."
  exit 0
fi

if ! pidof dockerd >/dev/null 2>&1; then
  echo "dockerd is not running. The next start uses iptables-legacy. net.bridge.bridge-nf-call-iptables=${bridge_nf} (left unchanged)."
  exit 0
fi

restart_dockerd() {
  if [ -d /run/systemd/system ] && systemctl is-active --quiet docker; then
    systemctl restart docker
    return
  fi

  local pid
  pid="$(pidof -s dockerd)"
  local -a cmd=()
  local part
  while IFS= read -r -d '' part || [ -n "$part" ]; do
    [ -n "$part" ] && cmd+=("$part")
  done < "/proc/${pid}/cmdline"

  kill "$pid"
  local i
  for i in $(seq 1 50); do
    if ! kill -0 "$pid" 2>/dev/null; then
      break
    fi
    sleep 0.2
  done
  if kill -0 "$pid" 2>/dev/null; then
    echo "dockerd pid ${pid} did not exit." >&2
    exit 1
  fi

  # Detach from this script so the daemon survives after the shell exits.
  # /tmp files created by the agent user can reject root on this VM, so the
  # log lives under /var/log, which this script creates as root.
  local log=/var/log/dockerd-bridge-fix.log
  touch "$log"
  setsid "${cmd[@]}" >>"$log" 2>&1 < /dev/null &
  for i in $(seq 1 50); do
    if docker info >/dev/null 2>&1; then
      return
    fi
    sleep 0.2
  done
  echo "dockerd did not accept API connections after restart." >&2
  exit 1
}

restart_dockerd
bridge_nf="$(cat /proc/sys/net/bridge/bridge-nf-call-iptables 2>/dev/null || echo missing)"
echo "Restarted dockerd so bridge rules are in iptables-legacy ($(iptables --version)). net.bridge.bridge-nf-call-iptables=${bridge_nf} (left unchanged)."
