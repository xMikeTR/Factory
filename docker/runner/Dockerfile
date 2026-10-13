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

# Mutation testing for C# (strand 4). Pinned: 5.0.0 needs a newer SDK than 9. Runs offline in verify; baked in.
ARG STRYKER_VERSION=4.16.0
RUN dotnet tool install dotnet-stryker --tool-path /opt/dotnet-tools ${STRYKER_VERSION:+--version $STRYKER_VERSION} \
 && chmod -R a+rX /opt/dotnet-tools \
 && dotnet-stryker --help >/dev/null

# Line coverage for CRAP (quality.coverage): coverlet.console runs the test project's dll, offline.
RUN dotnet tool install coverlet.console --tool-path /opt/dotnet-tools --version 10.1.0 \
 && chmod -R a+rX /opt/dotnet-tools

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

# Node for TypeScript targets (strand 5): `npm ci --ignore-scripts` runs in the restore window only, against
# the registry fixed below (RestoreGate passes --userconfig); eslint measures complexity; dependency-cruiser reads TS imports for quality.forbid; Playwright's browsers
# are baked in at one version, so QA runs offline and a repo's @playwright/test must match it.
COPY --from=node:22-bookworm-slim /usr/local/ /usr/local/
ARG PLAYWRIGHT_VERSION=1.49.1
ENV PLAYWRIGHT_BROWSERS_PATH=/opt/playwright
RUN npm install -g eslint@9 dependency-cruiser@16 typescript@5 playwright@${PLAYWRIGHT_VERSION} \
 && npx --no-install playwright install --with-deps chromium \
 && chmod -R a+rX /opt/playwright \
 && rm -rf /var/lib/apt/lists/*
# The implementer's browser self-check tool (ScopedWorkspace.PlaywrightCli runs `playwright-cli <subcommand>`).
RUN npm install -g @playwright/cli@0.1.22
# The CLI wants the 'chrome' channel by default; point it at the image's own chromium, headless, no sandbox (the container is the sandbox).
RUN ln -s "$(ls -d /opt/playwright/chromium-*/chrome-linux/chrome)" /opt/playwright/chromium-bin
ENV PLAYWRIGHT_MCP_EXECUTABLE_PATH=/opt/playwright/chromium-bin PLAYWRIGHT_MCP_BROWSER=chromium PLAYWRIGHT_MCP_NO_SANDBOX=1 PLAYWRIGHT_MCP_HEADLESS=1 PLAYWRIGHT_MCP_OUTPUT_DIR=/tmp/playwright-cli
COPY eslint/eslint.config.mjs /opt/factory/eslint/eslint.config.mjs
RUN cd /opt/factory/eslint && npm install --no-save --ignore-scripts typescript-eslint@8 typescript@5 eslint@9 \
 && chmod -R a+rX /opt/factory
# Token lint: raw values are banned where a design token belongs (FACTORY_TOKEN_LINT_CMD points stylelint at this config).
COPY stylelint/stylelint.config.json /opt/factory/stylelint/stylelint.config.json
RUN cd /opt/factory/stylelint && npm install --no-save --ignore-scripts stylelint@16 postcss-scss@4 stylelint-declaration-strict-value@1 \
 && chmod -R a+rX /opt/factory
RUN printf '%s\n' 'registry=https://registry.npmjs.org/' 'ignore-scripts=true' 'audit=false' 'fund=false' > /etc/factory/npmrc \
 && chmod a+r /etc/factory/npmrc

COPY --from=pobserve /PObserve.jar /opt/pobserve/PObserve.jar
COPY --from=pobserve-parser src/ /opt/pobserve/parser/
COPY --from=pobserve-parser pobserve-check /usr/local/bin/pobserve-check
RUN chmod -R a+rX /opt/pobserve && chmod a+rx /usr/local/bin/pobserve-check

WORKDIR /work
CMD ["sleep", "infinity"]
