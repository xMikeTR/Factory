// Complexity is syntax-only, so the factory measures it with its own config rather than the repo's rules (which may
// need plugins that are not installed). Baked into the image with typescript-eslint beside it.
import tseslint from "typescript-eslint";

export default [
  { files: ["**/*.ts", "**/*.tsx", "**/*.mts", "**/*.cts"], languageOptions: { parser: tseslint.parser } },
  { files: ["**/*.js", "**/*.jsx", "**/*.mjs", "**/*.cjs"] },
];
