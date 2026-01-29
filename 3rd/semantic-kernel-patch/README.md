# Semantic Kernel Patch

This directory serves as a patch layer for the [Semantic Kernel](https://github.com/microsoft/semantic-kernel) library.

## Rationale

We identified certain areas in the official Semantic Kernel library that required improvement or adjustment to better fit our needs. Due to the time-consuming nature of the upstream Pull Request process, we opted to implement these changes locally to maintain development velocity.

## Methodology

1. **Original Repository**: The official repository is included as a submodule in `../semantic-kernel` and is kept in its original, unmodified state.
2. **Patched Projects**: For any project that requires modification, we create a corresponding project in this directory.
3. **File Linking**:
   - **Unmodified Files**: We reference the original source files from the submodule using relative paths.
   - **Modified Files**: We host the modified versions of the files directly in this directory.

This approach allows us to seamlessly integrate updates from the upstream repository while preserving our custom modifications.