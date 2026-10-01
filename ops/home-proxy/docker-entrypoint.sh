#!/bin/sh
set -eu

credentials_file=/run/secrets/squid_users

if [ ! -s "$credentials_file" ]; then
    echo "ERRO: crie o arquivo secrets/users com scripts/New-Credentials.ps1 antes de iniciar." >&2
    exit 1
fi

squid -f /etc/squid/squid.conf -k parse
exec squid -N -f /etc/squid/squid.conf

