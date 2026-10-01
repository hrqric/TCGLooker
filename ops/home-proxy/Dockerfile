FROM ubuntu:24.04

ARG DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        apache2-utils \
        ca-certificates \
        squid \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /run/squid /var/log/squid /var/spool/squid \
    && chown -R proxy:proxy /run/squid /var/log/squid /var/spool/squid

COPY --chown=proxy:proxy config/squid.conf /etc/squid/squid.conf
COPY --chown=proxy:proxy docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh

RUN chmod 0555 /usr/local/bin/docker-entrypoint.sh

USER proxy

EXPOSE 3128

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["squid", "-f", "/etc/squid/squid.conf", "-k", "check"]

ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]

