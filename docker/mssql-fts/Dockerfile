FROM mcr.microsoft.com/mssql/server:2022-latest
USER root
RUN apt-get update && apt-get install -yq curl gnupg apt-transport-https && apt-get clean \
    && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
    && echo "deb [signed-by=/usr/share/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/ubuntu/22.04/mssql-server-2022 jammy main" > /etc/apt/sources.list.d/mssql-server-2022.list \
    && apt-get update && apt-get install -y mssql-server-fts && apt-get clean && rm -rf /var/lib/apt/lists/*
USER mssql
