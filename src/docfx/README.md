# DocFX configuration

This folder builds the API reference site for RetroXmsg. The site is published to GitHub Pages by
`.github/workflows/docs.yml` on every push to `main`.

## Build locally

```powershell
# one time
dotnet tool install -g docfx

# from the repository root; add --serve to preview at http://localhost:8080/
docfx src/docfx/docfx.json
```

The applications restore the RetroIO packages from the local folder feed beside the repositories
(see Prerequisites in the top-level README), so that feed has to exist before DocFX can compile
them.

## Layout

 - `docfx.json`: scans every `RetroXmsg.*/*.csproj` (the tests are not included) and writes
   YAML metadata into `api/`, then renders the site into `_site/`.
 - `index.md`: the landing page.
 - `toc.yml`: top-level navigation.
 - `api/index.md`: the API reference landing page.

`_site/` and `api/*.yml` are generated and git-ignored.

## Live site

<https://retrocorelabs.github.io/retroxmsg/>
