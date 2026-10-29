# Licence situation

This is **not a licence** for Cloudberry Kingdom. This repository holds three kinds of material,
and they are under different terms.

## 1. Cloudberry Kingdom (Pwnee Studios) — no licence granted

Everything under `Game/` comes from Pwnee Studios' public repository,
<https://github.com/PwneeStudios/Cloudberry-Kingdom> (commit `2223d1ce`, 2019-01-17). That repository carries
**no licence**, so all rights in the game's code and data stay with Pwnee Studios. Nothing in this repository
grants you any rights to them. The same applies to any page built from this repository: the `.wasm` file and
the assemblies under `_framework/` are the game's compiled code.

We keep these files here, and only here, in good faith, for these reasons:

- **It is a minimal subset.** It has only the C# files the browser build compiles and the engine-only data the
  game reads when it runs in its own placeholder mode (`LoadResources = false`, which Pwnee added in 2016):
  collision boxes instead of art. There is **no art, sound, music or video** here.
- **The source is already public.** Pwnee published it on GitHub.
- **We support a proper licence.** A request for an open-source licence is open upstream:
  [PwneeStudios/Cloudberry-Kingdom#173](https://github.com/PwneeStudios/Cloudberry-Kingdom/issues/173).
  If the rights holder adds one, it replaces this section.

**Rights holders:** if you hold rights to Cloudberry Kingdom and want any of this changed or removed, open an
issue on this repository and we will comply promptly. GitHub's DMCA process is also available.

The commit history keeps Pwnee's files apart from the changes made to them. The first commit is Pwnee's files
unmodified, and every change after it is a separate commit.

## 2. Changes and additions by others — MIT, as far as they are theirs

- **fera89's FNA revival patches** (the second commit) come from
  [fera89/cloudberry-kingdom-fna-revival](https://github.com/fera89/cloudberry-kingdom-fna-revival),
  MIT License, Copyright (c) 2026 Christian Bittencort.
- **PeerInfinity's additions**: the browser changes to `Game/` (the third commit onward), and everything under
  `build/`, plus `README.md`, `NOTICE.md` and this file. These are released under the MIT License below.

Both grants cover only the authors' own contributions. A change to a Pwnee file is still part of a Pwnee file,
and section 1 still applies to that file as a whole.

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of
the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
IN THE SOFTWARE.
```

## 3. Third-party components — their own licences

See [`NOTICE.md`](NOTICE.md). FNA (`external/FNA`, a git submodule) is under the Ms-PL. The native archives that
`build/build.sh` downloads, and the .NET runtime in a built page, have their own terms, listed there.
