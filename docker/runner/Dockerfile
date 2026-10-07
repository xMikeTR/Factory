# factory-s2-runner: where agent and verify commands run (DockerSandbox).
# .NET 9 SDK, the P toolchain (decision checks), a JDK and PObserve (`pobserve-check`, logs
# against P specs). No secrets are baked in or passed at run time; the container runs as the
# host user with --network none apart from the restore window.
# Build with build.sh: it passes tools/pobserve-parser as the `pobserve-parser` build context.

# The PObserve CLI is not published; build it from P at a pinned commit.
FROM gradle:8.5-jdk17 AS pobserve
ARG P_COMMIT=260da928f67569ca7a8c0039a02d0bad7cde6096
RUN git clone --filter=blob:none --no-checkout https://github.com/p-org/P.git /src \
 && git -C /src sparse-checkout set Src/PObserve/PObserve \
 && git -C /src checkout --quiet ${P_COMMIT}
WORKDIR /src/Src/PObserve/PObserve
RUN gradle shadowJar -x check --no-daemon --quiet \
 && cp build/libs/PObserve-*.jar /PObserve.jar

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

# `p compile` emits net8.0 projects. Without the .NET 8 packs the build fetches them from
# nuget.org, which verify commands cannot reach (--network none). Bake them in.
COPY --from=mcr.microsoft.com/dotnet/sdk:8.0 /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref/ /usr/share/dotnet/packs/Microsoft.NETCore.App.Ref/
COPY --from=mcr.microsoft.com/dotnet/sdk:8.0 /usr/share/dotnet/packs/Microsoft.NETCore.App.Host.linux-x64/ /usr/share/dotnet/packs/Microsoft.NETCore.App.Host.linux-x64/
COPY --from=mcr.microsoft.com/dotnet/sdk:8.0 /usr/share/dotnet/packs/Microsoft.AspNetCore.App.Ref/ /usr/share/dotnet/packs/Microsoft.AspNetCore.App.Ref/

# The restore window's only package source (RestoreGate passes --configfile and pins
# RestoreSources too): a repo's nuget.config, or one an agent writes, never chooses where
# packages come from while the container has network.
RUN mkdir -p /etc/factory && printf '%s\n' '<?xml version="1.0" encoding="utf-8"?>' '<configuration>' '  <packageSources>' \
      '    <clear />' '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />' \
      '  </packageSources>' '</configuration>' > /etc/factory/nuget.config \
 && chmod a+r /etc/factory/nuget.config

COPY --from=pobserve /PObserve.jar /opt/pobserve/PObserve.jar
COPY --from=pobserve-parser src/ /opt/pobserve/parser/
COPY --from=pobserve-parser pobserve-check /usr/local/bin/pobserve-check
RUN chmod -R a+rX /opt/pobserve && chmod a+rx /usr/local/bin/pobserve-check

WORKDIR /work
CMD ["sleep", "infinity"]
