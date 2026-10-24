# Contributing

## Development

1. Use Windows and install the .NET SDK specified in [global.json](global.json).
2. Run `./build.ps1 -Test` in PowerShell from the repository root.
3. Run `./build.ps1 -Publish` and `./run.ps1` to try the portable app.
4. For live audio checks, install VB-CABLE separately and follow [the setup guide](docs/SETUP.md).

The automated suite does not require an installed cable or physical microphone. Tests check the mixer, gain/pause behavior, settings, cable selection, process grouping, and failed-start cleanup. See [development notes](docs/DEVELOPMENT.md) for optional hardware diagnostics.

Keep changes focused. Preserve the rule that the router does not change Windows defaults, source-app volume, or application device assignments. Explain hardware-dependent checks you could not run. Do not include recordings, local settings, build outputs, or device inventories in a pull request.

GitHub Actions builds, tests, and packages the app on Windows. Workflow dependencies are pinned to commit SHAs; when updating an action, verify its official release and update both SHA and version comment.

## Create your GitHub repository

The source folder is ready for Git, but no remote or repository has been created for you. Create an empty repository on GitHub, then run:

```powershell
git init -b main
git add .
git commit -m "Initial Audio Router release"
git remote add origin YOUR_GITHUB_REPOSITORY_URL
git push -u origin main
```

Replace `YOUR_GITHUB_REPOSITORY_URL` with your new repository's HTTPS or SSH URL. Git may ask you to configure your name/email and sign in. Leave the GitHub repository empty initially; the source already contains its README, license, and ignore rules.

Build outputs and the local SDK are ignored. To distribute an executable, upload `artifacts/AudioRouter-win-x64.zip` to a GitHub Release, or download the build artifact from a successful Windows workflow run. The workflow does not create releases or publish anything automatically.

## License

Contributions are provided under the project's [MIT license](LICENSE). Third-party components retain their own licenses; see [notices](THIRD-PARTY-NOTICES.md).
