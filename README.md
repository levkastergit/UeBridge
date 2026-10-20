# UE Bridge

Программа для **ПК2** (где стоит Unreal Engine). Одной кнопкой готовит ПК2 так, чтобы Claude Code, установленный на **ПК1**, видел файлы проекта и управлял редактором через MCP (плагин [unreal-engine-skills-for-claude-code](https://github.com/EpicGames/unreal-engine-skills-for-claude-code-plugin)).

Claude Code при этом установлен и работает только на ПК1.

## Что делает кнопка «Настроить всё»

1. Включает стандартный компонент Windows **OpenSSH Server** (служба sshd, автозапуск, правило брандмауэра на порт 22 для частной сети).
2. Расшаривает папку проекта по сети (доступ только текущему пользователю Windows).
3. В `.uproject` включает плагины `ModelContextProtocol` и `AllToolsets` (оригинал сохраняется в `.uproject.bak`).
4. Пишет в `Saved/Config/WindowsEditor/EditorPerProjectUserSettings.ini` порт и путь MCP-сервера и автозапуск сервера.
5. Создаёт или дополняет `.mcp.json` в папке проекта записью `unreal-mcp`.
6. Проверяет, что в установленном движке есть плагин ModelContextProtocol.

Все шаги идемпотентны: кнопку можно нажимать повторно, лишнего она не сделает.

## Кнопка «Обновить»

Проверяет на GitHub ([Releases](../../releases)) новую версию программы. Если она есть, открывает страницу релиза, откуда нужно скачать `UeBridge.exe` и заменить файл. Затем заново применяет все настройки (то же, что «Настроить всё»).

## Для ПК1

После настройки окно «Данные и команды для ПК1» показывает имя ПК2, IP-адреса, имя пользователя для SSH и команды. Кнопка «Сохранить скрипт для ПК1…» создаёт `.cmd`, который на ПК1:

1. подключает сетевую папку проекта как диск (по умолчанию `Z:`);
2. поднимает SSH-туннель `ssh -N -L 8000:127.0.0.1:8000 user@ПК2`;
3. запускает `claude` в папке проекта.

Пароль для `net use` и `ssh` — пароль входа в Windows на ПК2.

Внутри Claude Code на ПК1:

```
/plugin install unreal-engine-skills-for-claude-code@claude-plugins-official
/mcp
```

## Требования

- ПК2: Windows 10/11, права администратора (программа сама запросит), Unreal Engine с плагином ModelContextProtocol.
- ПК1: Windows с клиентом `ssh` (входит в Windows 10/11), Claude Code, Git Bash для хука плагина.
- Оба ПК в одной локальной сети.

## Сборка

Нужен [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0):

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```

Релиз на GitHub: пуш тега `vX.Y.Z` (версия должна совпадать с `<Version>` в `UeBridge.csproj`) собирает exe через GitHub Actions и прикладывает его к релизу.
