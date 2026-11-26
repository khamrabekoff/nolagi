# pckit by odilbek (старая версия)

Здесь лежит прежняя версия программы — **pckit**, один файл `pckit-by-odilbek.cmd` с окном на PowerShell. Её заменил Nolagi; новые функции сюда не добавляются.

- `pckit-ui.ps1` — окно, `pckit.ps1` — командная строка, `PCKIT.md` — описание устройства.
- Движок (`engine/`, `data/`, `recipes/`) общий с Nolagi и лежит в корне репозитория.
- Сборка: `powershell -NoProfile -ExecutionPolicy Bypass -File .\legacy\build.ps1` → `legacy\dist\pckit-by-odilbek.cmd`. Скрипт заодно проверяет синтаксис, UTF-8 BOM и JSON во всём репозитории, поэтому его запускает CI.
- Старые выпуски: [Releases](https://github.com/khamrabekoff/nolagi/releases) (v2.2.0–v2.6.0).
