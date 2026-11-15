/**
 * Vitest browser mode for the UI contract (the app's side; the Factory writes no config).
 *
 *   ui-preview  renders ui-contract/tests/*.preview.test.tsx and writes the baselines people approve (run by `factory-s2 preview`)
 *
 * Baselines are generated in the runner image, never on a laptop: fonts differ, and the platform is part of the file name.
 */
import { defineConfig } from "vitest/config";
import { playwright } from "@vitest/browser-playwright";
import react from "@vitejs/plugin-react";
import { resolve } from "node:path";

const PREVIEWS = "ui-contract/previews";

const browser = () => ({
  enabled: true,
  headless: true,
  provider: playwright({ launchOptions: process.env.CHROME_PATH ? { executablePath: process.env.CHROME_PATH } : {} }),
  instances: [{ browser: "chromium" as const, viewport: { width: 1440, height: 900 } }],
  expect: {
    toMatchScreenshot: {
      // `ui__<screen>__<state>` -> previews/<screen>/<state>-<browser>-<platform>.png
      // `ui__<screen>__<state>__<placement>` -> previews/<screen>/placements/<state>__<placement>-<browser>-<platform>.png
      resolveScreenshotPath: ({ arg, ext, root, browserName, platform, testFileDirectory, screenshotDirectory, testFileName }: any) => {
        const m = /^ui__([a-z0-9-]+)__([a-z0-9-]+)(?:__([a-z0-9-]+))?$/.exec(arg);
        return m
          ? m[3]
            ? resolve(root, PREVIEWS, m[1], "placements", `${m[2]}__${m[3]}-${browserName}-${platform}${ext}`)
            : resolve(root, PREVIEWS, m[1], `${m[2]}-${browserName}-${platform}${ext}`)
          : resolve(root, testFileDirectory, screenshotDirectory, testFileName, `${arg}-${browserName}-${platform}${ext}`);
      },
    },
  },
});

export default defineConfig({
  plugins: [react()],
  test: {
    projects: [
      { extends: true, test: { name: "ui-preview", include: ["ui-contract/tests/*.preview.test.tsx"], browser: browser() } },
    ],
  },
});
