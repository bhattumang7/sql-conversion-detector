ARG BASE_IMAGE=mcr.microsoft.com/mssql/server:2022-latest
FROM ${BASE_IMAGE}
ARG MSSQL_REPO_YEAR=2022
USER root
RUN apt-get update && apt-get install -yq --no-install-recommends apt-transport-https curl gnupg && apt-get clean \
    && curl -fsSL --proto '=https' --tlsv1.2 https://packages.microsoft.com/keys/microsoft.asc | gpg --batch --yes --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
    && echo "deb [signed-by=/usr/share/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/ubuntu/22.04/mssql-server-${MSSQL_REPO_YEAR} jammy main" > /etc/apt/sources.list.d/mssql-server.list \
    && apt-get update \
    && cd /tmp && apt-get download mssql-server-fts mssql-server-polybase \
    && DEBIAN_FRONTEND=noninteractive dpkg -i --force-depends mssql-server-fts_*.deb mssql-server-polybase_*.deb \
    && rm -f /tmp/*.deb \
    && apt-get clean && rm -rf /var/lib/apt/lists/*
USER mssql
