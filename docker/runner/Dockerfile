# factory-s2-runner: where agent and verify commands run (DockerSandbox).
# .NET 9 SDK, the P toolchain (decision checks) and a JDK (PObserve's Java log parser).
# No secrets are baked in or passed at run time; the container runs as the host user with
# --network none apart from the restore window.
FROM mcr.microsoft.com/dotnet/sdk:9.0

RUN apt-get update \
 && apt-get install -y --no-install-recommends openjdk-17-jdk-headless procps \
 && rm -rf /var/lib/apt/lists/*

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_ROLL_FORWARD=Major \
    PATH="/opt/dotnet-tools:${PATH}"

ARG P_VERSION=3.1.0
RUN dotnet tool install P --tool-path /opt/dotnet-tools ${P_VERSION:+--version $P_VERSION} \
 && chmod -R a+rX /opt/dotnet-tools \
 && p --version

WORKDIR /work
CMD ["sleep", "infinity"]
