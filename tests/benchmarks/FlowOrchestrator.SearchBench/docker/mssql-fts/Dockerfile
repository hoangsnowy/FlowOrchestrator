# SQL Server 2022 + Full-Text Search, for the run-search investigation harness only.
# The stock mcr.microsoft.com/mssql/server image ships without the FTS daemon; Azure SQL Database
# has FTS built in, so this image is the closest local stand-in for "CONTAINS on Azure SQL".
#
#   docker build -t floworch-mssql-fts:2022 tests/benchmarks/FlowOrchestrator.SearchBench/docker/mssql-fts
FROM mcr.microsoft.com/mssql/server:2022-latest
USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl gnupg ca-certificates \
 && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
 && echo "deb [arch=amd64 signed-by=/usr/share/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/ubuntu/22.04/mssql-server-2022 jammy main" > /etc/apt/sources.list.d/mssql-server-2022.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends mssql-server-fts \
 && apt-get clean && rm -rf /var/lib/apt/lists/*
USER mssql
