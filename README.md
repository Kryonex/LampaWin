<div align="center">
  <img src="src/LampaWin.Desktop/Assets/LampaWin-Logo.svg" width="112" alt="Логотип LampaWin">
  <h1>LampaWin</h1>
  <p><strong>Домашний медиацентр в одном окне Windows</strong></p>
  <p>Каталог Lampa, поиск через Jackett, TorrServer и полноэкранное видео в LibVLC — без Docker и ручной настройки локальных сервисов.</p>

  <p>
    <a href="https://github.com/Kryonex/LampaWin/releases/latest"><img src="https://img.shields.io/github/v/release/Kryonex/LampaWin?display_name=tag&label=релиз" alt="Последний релиз"></a>
    <a href="https://github.com/Kryonex/LampaWin/blob/main/LICENSE"><img src="https://img.shields.io/github/license/Kryonex/LampaWin?label=лицензия" alt="Лицензия"></a>
    <img src="https://img.shields.io/badge/Windows-10%2F11%20x64-5965f2" alt="Windows 10 и 11, x64">
  </p>

  <p>
    <a href="https://github.com/Kryonex/LampaWin/releases/latest/download/LampaWin-Setup-win-x64.exe"><strong>⬇ Скачать установщик</strong></a>
    &nbsp; · &nbsp;
    <a href="https://github.com/Kryonex/LampaWin/releases/latest/download/LampaWin-win-x64-portable.zip">Портативная версия</a>
    &nbsp; · &nbsp;
    <a href="https://github.com/Kryonex/LampaWin/releases/latest/download/SHA256SUMS.txt">SHA-256</a>
    &nbsp; · &nbsp;
    <a href="VALIDATION.md">Проверки и совместимость</a>
  </p>
</div>

---

## Всё нужное для просмотра — вместе

| Возможность | Что это даёт |
| --- | --- |
| **Каталог Lampa** | Знакомый интерфейс, отдельный профиль и автоматическое подключение локальных сервисов. |
| **Поиск и торренты** | Jackett и TorrServer запускаются вместе с приложением и работают в рамках пользовательского профиля. |
| **Встроенный плеер** | Видео воспроизводится через LibVLC; прогресс просмотра передаётся обратно в Lampa. |
| **Удобное управление** | Пауза, перемотка, громкость, полноэкранный режим, аудиодорожки и субтитры — с клавиатуры и из панели плеера. |
| **Простая установка** | Установщик для текущего пользователя; Docker, отдельная установка VLC и .NET не нужны. |

## Установка

1. Скачайте **[LampaWin-Setup-win-x64.exe](https://github.com/Kryonex/LampaWin/releases/latest/download/LampaWin-Setup-win-x64.exe)** из раздела [Releases](https://github.com/Kryonex/LampaWin/releases/latest).
2. Запустите установщик и откройте LampaWin из меню «Пуск».
3. Выберите каталог и откройте видео через раздел торрентов Lampa.

Установка не требует прав администратора. Если Microsoft WebView2 Runtime ещё не установлен, установщик загрузит его с серверов Microsoft — потребуется интернет. Каталоги, поиск и сторонние источники также работают через интернет.

### Портативный запуск

Скачайте [LampaWin-win-x64-portable.zip](https://github.com/Kryonex/LampaWin/releases/latest/download/LampaWin-win-x64-portable.zip), распакуйте архив и запустите `LampaWin.exe`. На компьютере должен быть установлен Microsoft WebView2 Runtime. Пользовательские данные хранятся отдельно от папки приложения.

## Управление плеером

| Клавиша | Действие |
| --- | --- |
| `Space` или `K` | Пауза / воспроизведение |
| `J` / `L` или `←` / `→` | Перемотка на 10 секунд |
| `M` | Выключить или включить звук |
| `↑` / `↓` | Изменить громкость |
| `F` или `F11` | Полноэкранный режим |
| `Esc` | Закрыть настройки или полный экран; затем вернуться в каталог |

Панель управления появляется при движении мыши и скрывается после короткого бездействия. Нажмите на видео, чтобы поставить его на паузу; двойное нажатие включает полный экран. Шестерёнка открывает выбор аудиодорожек и субтитров.

## Локальная работа и данные

LampaWin запускает дочерние процессы на время своей работы и завершает их при закрытии. Локальные HTTP-интерфейсы привязаны к `127.0.0.1`; TorrServer использует сеть для получения данных. Приложение не устанавливает службы или автозапуск Windows и не меняет настройки брандмауэра.

Настройки, история, конфигурации и журналы находятся в `%LOCALAPPDATA%\LampaWin`. Снимок настроек Lampa защищён Windows DPAPI для текущего пользователя. Удаление приложения сохраняет пользовательские данные.

## Совместимость и ограничения

- Windows 10 версии 1809 или новее, Windows x64.
- Windows ARM64 и 32-битная Windows не поддерживаются.
- Сборка не подписана сертификатом издателя, поэтому Windows может показать предупреждение SmartScreen.
- Доступность каталогов, трекеров и индексаторов зависит от их владельцев, сети и ограничений сторонних сайтов.
- Ручные проверки на чистой Windows и воспроизведение реальных раздач описаны отдельно: **[отчёт о совместимости](VALIDATION.md)**.

Используйте приложение только для контента, к которому у вас есть законный доступ.

## Сборка из исходников

Нужны .NET SDK 10, PowerShell, Node.js/npm и Git. Скрипт подготовки получает закреплённые версии компонентов и проверяет их контрольные суммы. NuGet-пакеты восстанавливаются из NuGet.org.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/packaging/Acquire-Components.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tools/packaging/Build-Release.ps1
```

Готовые файлы появятся в `artifacts/`: установщик и портативный ZIP. Подробности автоматических проверок и известных ограничений — в [VALIDATION.md](VALIDATION.md).

### Публикация обновления

Чтобы выпустить новую версию, измени `Version`, `FileVersion` и `InformationalVersion` в `src/LampaWin.Desktop/LampaWin.Desktop.csproj` и отправь изменения в ветку `main`. Например, для версии `1.0.3`:

```powershell
git add src/LampaWin.Desktop/LampaWin.Desktop.csproj
git commit -m "Release LampaWin 1.0.3"
git push origin main
```

Если для этой версии ещё нет релиза, GitHub Actions автоматически соберёт установщик и портативный архив и создаст `v1.0.3` GitHub Release с SHA-256. Установленная LampaWin при следующем запуске предложит обновление; после согласия загрузит установщик, проверит контрольную сумму, установит новую версию и перезапустится. Профиль и настройки в `%LOCALAPPDATA%\LampaWin` сохраняются. Портативную копию нужно обновить вручную.

## Лицензии

Собственный код LampaWin распространяется под MIT. Lampa и Jackett используют GPL-2.0, TorrServer — GPL-3.0, LibVLC и LibVLCSharp — LGPL. Условия и сведения о включённых компонентах приведены в [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) и в дистрибутиве. Лицензия LampaWin не заменяет лицензии сторонних компонентов.
