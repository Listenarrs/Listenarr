# Six Labors Community license

`sixlabors.lic` is the original signed, assembly-scoped Community license for Listenarr. Six Labors permits this type of key in public repositories so contributors and CI can build the licensed assemblies.

- License ID: `ctm_01m5yzjgf8j1ghjwtn830dbka3`
- Expires: January 25, 2028
- Licensed assemblies requested: `Listenarr.Infrastructure`, `Listenarr.Api`, and `Listenarr.Tests`
- Renewal portal: https://licensing.sixlabors.com/
- License configuration: https://docs.sixlabors.com/articles/imagesharp/

The shared `Directory.Build.props` points builds at this file, including container builds. Keep the supplied license contents unchanged. Renew during the final 90 days before expiry and replace this file with the renewed assembly-scoped license.

Only this approved assembly-scoped file is allowed by the repository and Docker ignore rules. Keep unrestricted Community and commercial keys private. Local private license files can be stored in the ignored `.licenses/` folder and selected explicitly with `SixLaborsLicenseFile`; CI can use the `SIXLABORS_LICENSE_KEY` environment variable containing the full supplied license string.
