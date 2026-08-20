# AGO Platform

[![CI](https://github.com/golyakoff/ago-platform/actions/workflows/ci.yml/badge.svg)](https://github.com/golyakoff/ago-platform/actions/workflows/ci.yml)

The reusable substrate behind AGO products: hosting, realtime transport, messaging, persistence,
caching, object storage, resilience and observability. It knows nothing about any product, and it
cannot: products are separate repositories, and this one ships as versioned NuGet packages.

Projects arrive with the work that needs them (roadmap Stage 0 onward); the intended set is listed in
`../ago-root/docs/conventions/naming-and-structure.md`.

## Rules

Architecture, layering and decisions are not documented here — they live in the root repository:

- Layering and what goes where: `../ago-root/docs/architecture/clean-architecture.md`
- Why the platform is a package, not a folder: `../ago-root/docs/adr/0012-*`
- Resilience policies: `../ago-root/docs/architecture/resilience.md`
- Working agreements for humans and AI sessions: `../ago-root/CLAUDE.md`

If code here contradicts that repository, the code is wrong.

## The one rule that defines this repository

Nothing in it may know that AGO Chat exists. Anything that needs to belongs in the product.
