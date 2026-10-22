# Contributing

Thanks for helping. This is a one-person project, so small, focused changes land fastest.

## Most wanted: device reports

The app is verified on one TV. A report from any other model is the most valuable contribution,
whether it worked or not. Open an issue with the **Device report** template and include:

* the model code (e.g. `QN65S90D`), the year, and the Tizen version (Settings → Support → About This TV)
* the `tv` line and the `last_status` from a telemetry report (`tools/stats-receiver.py`), or what the
  strip did if you have no collector running

## Code changes

1. Development happens on [GitLab](https://gitlab.com/ritwick7/TizenAmbilight); merge requests there
   are preferred. The GitHub repository is a mirror, but pull requests there are also read.
2. Branch from `master`. Use [Conventional Commits](https://www.conventionalcommits.org/)
   (`fix:`, `feat:`, `docs:`, `refactor:`, `ci:`), with a body that explains *why*.
3. Before pushing:

   ```bash
   dotnet run --project decoder-selftest/DecoderSelfTest.csproj      # must end "FAILED 0"
   dotnet msbuild TizenAmbilight/TizenAmbilight.csproj -restore -t:Rebuild -p:Configuration=Release
   python3 tools/check-secrets.py --staged
   ```

4. If the change alters behaviour on the TV, bump `Ambilight.Version` in `Program.cs` and say in the
   MR how you verified it on a device. If you could not, say that too.

House rules:

* **No NuGet dependencies.** Codecs, JSON and P/Invoke are first-party on purpose.
* **Never commit addresses, certificates or tokens.** `config.json`, `*.p12`, `profiles.xml` and
  similar files are gitignored; keep it that way.
* Comments record *why*. Several describe a past bug, so update them when you change the behaviour.
* Control tokens take **numbers only** and must validate their range in `Control.cs`.
* New shared source files go into `decoder-selftest/DecoderSelfTest.csproj` so the harness compiles
  them.

## Reporting bugs

Include the TV model, the app version (`version` in telemetry), your `config.json` with the IPs
removed, and the `log_tail` of a telemetry report if you have one. For security issues, see
[SECURITY.md](SECURITY.md).
