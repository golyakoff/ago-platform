# Changelog

All notable changes to `Ago.Platform.*` are recorded here. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning is
[SemVer](https://semver.org/) (`docs/architecture/repositories.md`).

## [0.1.0] - 2026-08-20

### Added

- `Ago.Platform.Kernel`: `Result`, `Result<T>`, `Error`, `IStronglyTypedId`, `IClock`,
  `IIdGenerator` and its default `UuidV7Generator`.
- `Ago.Platform.Hosting`: `IProductModule`, the platform/product seam a host loads.
