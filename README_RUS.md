# Abyssal Garden · Godot

[English README](README.md)

Порт Abyssal Garden на Godot .NET и C#. Это процедурный подводный мир с лесами ламинарии, затонувшим кораблём, мантами и объёмными световыми лучами. Порт основан на браузерном демо Максимилиана Дендуры (Noniv). [Оригинальный репозиторий](https://github.com/Noniv/abyssal-garden) · [Браузерная версия](https://noniv.github.io/abyssal-garden/)

## Запуск проекта

Сгенерируйте мир и шейдеры на компьютере с Node.js 24 и GPU с поддержкой WebGPU. Исходный генератор находится в `source/`.

```powershell
git clone https://github.com/warlock-su/abyssal-garden-godot.git
cd abyssal-garden-godot
npm install --prefix .godot/port-tools --no-audit --no-fund naga-wasi-cli@0.1.0 webgpu@0.6.2
node tools/bake-abyssal.mjs . .godot/port-tools/node_modules
```

Откройте `project.godot` в Godot **4.8 beta 3 .NET** с установленным **SDK .NET 10** и нажмите **F5**. Проект проверен на Windows с Forward+ и Vulkan. [Загрузки Godot](https://godotengine.org/download/archive/4.8-beta3/)

В главном меню и меню паузы доступны русский и английский языки, пресеты размера окна и три уровня детализации. Рендер следует за видимой областью окна пиксель в пиксель. Для проверки пресетов из редактора запускайте игру в отдельном окне. [Встраивание окна игры в Godot](https://docs.godotengine.org/en/stable/tutorials/editor/game_embedding.html)

| Управление | Действие |
| --- | --- |
| Мышь, WASD | Обзор и плавание |
| E / Q, Shift | Подъём, спуск и ускорение |
| 1–6 | Переход к точке обзора |
| Esc, M, H | Пауза, переключение звука и подробная статистика |
| F, B | Полёт по маршруту и 60-секундный бенчмарк |

Для сборки под Windows x64 установите **шаблоны экспорта .NET** той же версии, что и редактор, и используйте пресет **Windows Desktop**. Запускайте `build/windows/AbyssalGarden.exe` рядом с его файлом PCK и каталогом данных .NET.

## Рендеринг и замеры

Godot `RenderingDevice` рисует инстансы, отбирает видимую геометрию и переключает LOD. Тени, вода с каустикой, объёмные лучи и bloom рисуются отдельными GPU-проходами. Исходные шейдеры WGSL компилируются в SPIR-V. Камера, столкновения, синтез звука, интерфейс и рендеринг работают на C#. Данные трёх профилей мира заранее готовит исходный генератор.

В таблице указаны **среднее / p99** в миллисекундах для одного 60-секундного маршрута на RTX 5090 при High. Chrome 157 использовал WebGPU, Godot 4.8 beta 3 — Vulkan. При 2560×1440 Godot рисовал буфер полного разрешения и выводил его в окно 1280×720.

| Разрешение рендера | Кадр Chrome | Кадр Godot | GPU Chrome | GPU Godot |
| --- | ---: | ---: | ---: | ---: |
| 1280×720 | 1,60 / 2,00 | 1,56 / 1,85 | 1,58 / 1,90 | 1,54 / 1,82 |
| 2560×1440 | 2,52 / 3,10 | 2,34 / 2,78 | 2,50 / 2,97 | 2,32 / 2,75 |

На шести контрольных кадрах при 1280×720 и High SSIM составил **0,99138–0,99725**, а средняя абсолютная разница по RGB — **0,160–0,364** из 255 на канал. [Команды для повторения замеров](README.md#rendering-and-benchmarks)

## Генерация ресурсов

Команда готовит High, Medium и Low. Передайте `0`, `1` или `2`, чтобы собрать один профиль, либо `--shaders-only`, чтобы обновить SPIR-V. Сгенерированные файлы хранятся локально в `assets/abyssal/`. [Исходная ревизия](https://github.com/Noniv/abyssal-garden/tree/a596354eff400d458bc2aeb9bdf5e0a9734b2bcf)

## Авторы и лицензия

Оригинальный мир, генератор и шейдеры создал Maksymilian Dendura (Noniv). Порт на Godot подготовил Warlock Su. Форк сохраняет исходное уведомление об авторских правах и текст лицензии MIT в [LICENSE](LICENSE). Уведомление для ресурсов мира хранится в [assets/abyssal/LICENSE.txt](assets/abyssal/LICENSE.txt). [Оригинальный проект](https://github.com/Noniv/abyssal-garden)
