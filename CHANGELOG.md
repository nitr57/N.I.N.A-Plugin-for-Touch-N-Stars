# Touch 'N' Stars

## Unreleased
- Keep advertising the `_touchnstars._tcp` service and its active port on
  Linux while sharing hostname ownership with the system Avahi daemon.
- Package and serve the `celestia-atlas-data` application directory.
- Keep generated Celestia Atlas landscapes in persistent N.I.N.A. user data and
  migrate existing app-local landscapes without overwriting user content.

## 1.4.0.1
- fix(filesystem-preview): read the Bayer pattern from the file header, so OSC FITS/XISF frames offer the debayer option and render with their actual pattern

## 1.4.0.0
- feat(filesystem): add server-side preview/imageinfo endpoints

## 1.3.2.0
- Update Touch N Stars WebApp

## 1.3.1.0
- Update Touch N Stars WebApp

## 1.3.0.0
- Generated Celestia Atlas landscapes are now stored in the persistent N.I.N.A. user data; existing app-local landscapes are migrated without overwriting user content
- The `celestia-atlas-data` directory is now packaged and served

## 1.0.0.1

- Initial release
