#!/usr/bin/env bash
# DRAFT host bootstrap for the Oracle Ampere A1 VM. Not Terraform, not a
# cloud account action, and not run by CI. Review it before running it as root.
# Pair this with oracle-vm-cloud-init.draft.yaml. Keep the two drafts in step.
#
# Installs Docker, age, and rclone, turns on unattended upgrades, sets ufw to
# deny inbound except SSH, and drops new connections that reach Docker through
# the public NIC. This compose file publishes no host ports; the iptables
# rules are there because Docker otherwise bypasses ufw.
set -euo pipefail

if [[ "$(id -u)" != "0" ]]; then
  printf 'bootstrap: run as root\n' >&2
  exit 1
fi

export DEBIAN_FRONTEND=noninteractive

printf 'unattended-upgrades unattended-upgrades/enable_auto_updates boolean true\n' \
  | debconf-set-selections

apt-get update
apt-get install -y \
  docker.io \
  docker-compose-v2 \
  unattended-upgrades \
  ufw \
  age \
  rclone \
  ca-certificates \
  iptables

systemctl enable --now docker
systemctl enable --now unattended-upgrades

ufw default deny incoming
ufw default allow outgoing
ufw allow OpenSSH
ufw --force enable

iface=$(ip route show default | awk '{print $5; exit}')
if [[ ! "$iface" =~ ^[A-Za-z0-9._-]+$ ]]; then
  printf 'bootstrap: refusing unexpected interface name\n' >&2
  exit 1
fi

apply_docker_user() {
  local bin=$1
  "$bin" -N DOCKER-USER 2>/dev/null || true
  "$bin" -F DOCKER-USER
  "$bin" -A DOCKER-USER -i "$iface" -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN
  "$bin" -A DOCKER-USER -i "$iface" -j DROP
  "$bin" -A DOCKER-USER -j RETURN
}

apply_docker_user iptables
apply_docker_user ip6tables

install -d -m 755 /usr/local/sbin /etc/systemd/system
cat >/usr/local/sbin/worklane-docker-user-deny.sh <<EOF
#!/bin/sh
set -eu
iface=$iface
apply() {
  "\$1" -N DOCKER-USER 2>/dev/null || true
  "\$1" -F DOCKER-USER
  "\$1" -A DOCKER-USER -i "\$iface" -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN
  "\$1" -A DOCKER-USER -i "\$iface" -j DROP
  "\$1" -A DOCKER-USER -j RETURN
}
apply iptables
apply ip6tables
EOF
chmod 755 /usr/local/sbin/worklane-docker-user-deny.sh

cat >/etc/systemd/system/worklane-docker-user-deny.service <<EOF
[Unit]
Description=Reapply Worklane DOCKER-USER drop
After=docker.service
Requires=docker.service

[Service]
Type=oneshot
ExecStart=/usr/local/sbin/worklane-docker-user-deny.sh
RemainAfterExit=yes

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable worklane-docker-user-deny.service

printf 'bootstrap: draft lockdown applied on %s. SSH stays open. No 80, 443, or 5432.\n' "$iface"
printf 'bootstrap: this did not create cloud accounts, buckets, or tunnels.\n'
