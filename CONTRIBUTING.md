# Contributing

Patches, bug reports, and corrections to the README's comparison table are all welcome. Open an
issue first for anything larger than a fix, so we don't both build the same thing.

## Ground rules

- `dotnet test` must pass. New behavior comes with tests — see the existing suites for the
  house style (pure, offline, no hardware).
- Anything touching the device protocol should cite `PROTOCOL.md`, and update it if the wire
  behavior turns out to be different from what's documented there.
- Device writes are destructive. Write paths verify by read-back and roll back on failure.

## Copyright and licensing

NAMager is GPLv3, and will stay GPLv3.

One extra thing is asked of contributors: by opening a pull request, you agree that your
contribution is licensed to the project under the GPLv3 **and** that Ed Hubbell may also
distribute it under a separate commercial license.

The reason is specific and worth stating plainly. NAMager exists because a hardware vendor's
own software isn't as good as it could be. A possible good outcome for this project is that the
vendor eventually wants to license or ship it. That conversation is only possible while one
person holds the copyright to all of it. Without this grant, a single merged patch permanently
removes that option.

If you'd rather not grant that, say so in the PR. A fix can usually be reimplemented from a
description, or taken as a suggestion in an issue instead - no hard feelings either way. And you can always make your own fork that takes advantage of the existing work. 

Add a `Signed-off-by:` line to your commits (`git commit -s`) to indicate you have the right to
submit the work.
