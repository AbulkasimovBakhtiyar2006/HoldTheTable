# HOLD THE TABLE — ONE-HOUR BANZAI BUILD

Это не обычный размеренный процесс разработки игры. Это часовой инженерный штурм: мультиагенты, короткие циклы интеграции, полный игровой цикл и настоящий APK. «Камикадзе / банзай» здесь означает темп исполнения. Цель — работающий результат, подтверждённый сборкой и проверками.

**Claude: сначала прочитай `CLAUDE.md`, затем выполняй спецификацию ниже. Час начинается с первого действия по реализации, а не с создания этих документов.**

## Исходное состояние

Пользователь сообщает: Unity установлен и открыт, Quest 3S подключён, необходимые загрузки уже выполнены. Не переустанавливай всё с нуля. Найди открытый Unity-проект и используй его совместимые пакеты.

При подготовке этих инструкций 18 ноября 2026 года в этой папке обнаружены `.git`, `.idea` и `TableCiv.iml`; `Assets`, `Packages/manifest.json` и `ProjectSettings/ProjectVersion.txt` отсутствовали. Это папка инструкций, пока не найден настоящий Unity project root. Не выдавай этот каталог за проверенный Unity-проект. Сначала проверь запущенный Unity и его `-projectPath`, затем ближайшие каталоги. Если найден другой корень, сохрани здесь `PROJECT_LOCATION.md` с абсолютным путём и положи туда эти инструкции, не затирая существующие инструкции.

## Результат за 60 минут

Полный играбельный hands-only MR prototype для Quest 3/3S, APK, исходники, созданная программно сцена, валидатор, отчёт проверки и четыре submission-документа. Все функции ниже обязательны. Простая реализация разрешена; заглушки и молчаливое удаление функций — нет. Бюджет времени не превращает непроверенное в готовое.

## ОБЯЗАТЕЛЬНОЕ ОБНОВЛЕНИЕ: AR-ИМПЕРИЯ И СВИТОК

Это новое обязательное требование пользователя, а не будущая идея. При конфликте с исходной спецификацией ниже этот раздел имеет приоритет: автоматическое появление замка на одном столе заменяется выбором цивилизации и размещением первой ратуши самим игроком. Все прежние боевые функции сохраняются.

**Проект — AR:** игрок видит свою настоящую комнату через passthrough, а свиток, поселение, знамя и враги находятся внутри неё. На Quest используется passthrough MR. Не заменять комнату непрозрачным виртуальным окружением. Игрок может выбрать подходящее место по всей комнате, а не только на заранее определённом столе.

### Новый обязательный старт игры

1. Рядом с игроком в пределах удобной досягаемости разворачивается красивый **императорский свиток**: тёплый пергамент, закрученные края, деревянные валики, золотой орнамент, восковая печать и мягкая анимация раскрытия. Свиток висит в пространстве, переносится pinch-grab и содержит крупные читаемые poke-кнопки. Это отдельный world-space объект; правило крепления боевого HUD к запястью на него не распространяется. Визуал создаётся процедурно.
2. Прямо на свитке игрок пальцем выбирает свою цивилизацию. Минимум три вымышленных варианта: `Celestial Dynasty`, `Verdant Clans`, `Iron Dominion`; у каждого краткое описание и архитектурный мотив — ярусные крыши, деревянные формы или каменные башни. Выбор действительно сохраняется в профиле и меняет внешний вид ратуши и построек. Собственное имя цивилизации задаётся независимо от выбранного варианта.
3. Игрок вводит название, например **Heavenly Kingdom**, через встроенную poke-клавиатуру на свитке: буквы, пробел, backspace и подтверждение. Длина после trim — 1–32 символа; пустое имя заменяется предложенным. Предложенное имя можно оставить или изменить. Ввод полностью руками, без обязательных телефона, компьютера, голоса или сети. Пользовательское имя является данными и не обязано быть английским; интерфейсные строки остаются английскими и централизованными.
4. На свитке игрок выбирает цвет полотна и цвет символа, видит предпросмотр знамени и **рисует свой символ кончиком указательного пальца** прямо на поле для рисования. Контакт начинает штрих, отрыв заканчивает; движение рядом с полем не рисует. Нужны `Undo`, `Clear`, толщина линии и `Confirm`. Штрихи ограничены рамкой поля; потеря tracking завершает штрих без длинного соединительного следа. Пустой рисунок допустим: используется предложенный герб, чтобы не блокировать начало игры. Предпросмотр и знамя используют один рисунок, а не похожие подменные картинки.
5. Poke по `Found Empire` выдаёт миниатюру первого **Императорского дома — главной ратуши**. Игрок вытаскивает её из свитка pinch-grab и сам ставит в выбранном месте комнаты. Близкое размещение — рукой, дальнее — hand ray с pinch/confirm; вставать и брать контроллеры не нужно. Свиток не перехватывает отпускание миниатюры.
6. «В любом месте» означает выбранный игроком пригодный участок горизонтальной поверхности: стол, пол, тумба или достаточно большая полка. Проверяй опору под всей постройкой, границы и препятствия; показывай green/red ghost, разрешай отменить и выбрать другое место. Не ограничивай выбор одной столешницей или точкой перед камерой. Ратуша появляется ровно один раз после подтверждения. До этого не запускаются волны, доход timber или build countdown.
7. Рядом с поставленной ратушей появляется древко с **развевающимся знаменем** выбранного цвета и собственным нарисованным символом игрока; рядом отображается название цивилизации. Используй лёгкую процедурную деформацию полотна с закреплённым краем у древка, без тяжёлой cloth simulation. Эмблема движется вместе с тканью и читается с обеих сторон. Статичная картинка вместо развевающегося знамени не считается реализацией.
8. После основания открываются строительство и первая 12-секундная подготовка. Ратуша заменяет прежний keep, обладает его HP и является условием поражения. Поселение растёт от неё: начальная зона строительства радиусом 0.45 м, каждая постройка добавляет связанную область радиусом 0.20 м на выбранной поверхности. Все footprints должны иметь опору; запрети пересечения и строительство через обнаруженные разрывы. Максимум 12 оборонительных построек; ратуша и знамя не входят в лимит. Tray и Quartermaster располагаются относительно выбранного поселения. На большой поверхности, включая пол, враги появляются у локальной границы поля вокруг поселения, а не у дальних стен комнаты.
9. `RESTART` сбрасывает боевую сессию, сохраняя выбранную цивилизацию, имя, цвета, рисунок и место ратуши. На свитке доступно `New Empire`: остановить текущую сессию, убрать её объекты и заново пройти создание и размещение. Повторное открытие свитка и restart не создают дубликаты ратуши, знамени, подписок или корутин. Профиль хранится локально offline; последнее оформление подставляется при следующем запуске, а место основания в новой сессии подтверждается заново.

### Код, интеграция и проверка нового сценария

- Runtime: `CivilizationDefinition`, `CivilizationProfile`, `EmpireFoundationController`, `SettlementBuildArea`, `BannerRenderer`. UI: `ImperialScroll`, `CivilizationSelector`, `HandPokeKeyboard`, `EmblemDrawingCanvas`, `BannerCustomizer`. Все интерфейсные строки — в `Strings.cs`, имя и рисунок — пользовательские данные.
- SceneBuilder создаёт свиток, выбор, клавиатуру, drawing surface, токен ратуши и banner prefab; реальную ратушу не создаёт до подтверждения игроком. Validator проверяет компоненты, ссылки, события и наличие трёх civilization definitions. Классы без wiring не считаются работающей функцией.
- Состояния GameDirector: `CivilizationSetup → HeadquartersPlacement → BuildPhase → Wave → Upgrade/BuildPhase → Victory/Defeat`. `New Empire` возвращает в `CivilizationSetup`. Старый автоматический spawn keep должен быть удалён из стартового потока.
- Дополнение к гейту 2: свиток, выбор цивилизации, ввод имени, рисунок и размещение ратуши с правильным знаменем. К гейту 4: связанное расширение поселения. К гейту 7: визуальная отделка свитка и анимация знамени. К гейту 8: тот же сценарий основания на fallback; passthrough остаётся видимым. Агенты готовят эти части параллельно с исходными задачами.
- Обязательный acceptance test: руками выбрать цивилизацию → ввести `Heavenly Kingdom` → выбрать цвета → нарисовать свой символ → поставить ратушу → увидеть собственную эмблему на развевающемся знамени → построить оборону и начать волну.
- Дополнительно проверить основание на столе и вне стола, перенос свитка, неверную опору, undo/clear, потерю tracking при рисовании, локальное сохранение профиля, restart и `New Empire`. На подключённом Quest 3S проверяется весь hands-only сценарий; совместимость с Quest 3 сохраняется. Не выдавать автоматические вызовы методов за физическую проверку руками.
- Обновить CHANGELOG, SUBMISSION и SHOTLIST: показать AR-свиток, создание собственной цивилизации и герба, свободный выбор места ратуши и строительство от неё. Эти функции обязательны для Definition of Done.

## MISSION
Build "HOLD THE TABLE": a seated, hands-only AR empire-building and siege-defense game for Meta Quest 3/3S. Keep the real room visible through passthrough. Begin with the Imperial Scroll: civilization choice, custom name, banner colors and fingertip-drawn emblem. The player claims and places the first Imperial Town Hall on a suitable surface anywhere in the room, then expands the settlement from it. The Town Hall is the keep. Waves approach from the selected local battlefield boundary; surface edges and gaps shape the terrain. Preserve walls, archer towers, barricades, timber, three waves, two-charge palm-sweep shockwave, Quartermaster upgrades, score, win/lose and restart. See the mandatory AR founding specification above for the complete starting flow.

## NON-NEGOTIABLE RULES
1. NEVER cut a feature to save time. If a feature is hard, implement the crude version NOW and refine later. Deleting scope is forbidden. The only permitted degradation is "crude but working", never "absent".
2. Hands only, end to end. A user must complete the entire experience having never paired a controller. Keep controller support compiled in but never required, never referenced in UI.
3. Everything must work offline. No network calls at runtime. No cloud LLM.
4. All in-game strings in English, in one central static class. No hardcoded strings elsewhere.
5. Never use paid or editor-only assets. Only Meta SDK, Unity built-ins, and self-made primitives/procedural meshes. No Asset Store downloads, no .fbx dependencies.
6. 60 fps minimum with 30 live units. No realtime shadows, no post-processing stack, no reflection probes.
7. If a step blocks you for 2 minutes, report one line: BLOCKED | evidence | A: fastest viable fix | B: alternative | choosing A. Execute A immediately. Never hide the blocker or mark an unpassed gate as passed.
8. Commit-worthy state at every stage. Never leave the project uncompilable for more than one edit cycle.

## TECH STACK (locked, do not substitute)
- Unity LTS already installed in this project; if the project is empty, use Unity 2022.3 LTS.
- Packages, exact IDs, added via manifest: com.meta.xr.sdk.all (v81 or newer), com.meta.xr.mrutilitykit, com.unity.textmeshpro, com.unity.xr.management.
- XR: OpenXR with Meta Quest Support feature enabled, hand tracking feature enabled, passthrough enabled, Android target, ARM64, IL2CPP, linear color, Vulkan first with GLES3 fallback.
- Interactions: Meta Interaction SDK — HandGrabInteractable, HandGrabInteractor, PokeInteractable, RayInteractor for gaze-free far selection. Grab poses authored as HandPose ScriptableObjects, default pinch pose acceptable.
- MR: MRUK for room/plane discovery and anchoring. Spatial anchors to persist the castle position within the session.
- No NavMesh. Monsters follow a Catmull-Rom spline from a spawn point at the table edge to the keep, with simple separation steering. This is the crude version and it is final.

## SCENE AND CODE LAYOUT
Generate everything programmatically. Do not expect manual clicking.
- Assets/Scripts/Runtime/ — GameDirector, WaveSpawner, UnitDef, PlacedUnit, MonsterUnit, Health, SplinePath, TraySlot, ShockwaveAbility, QuartermasterAgent, ScoreKeeper, TableAnchorBootstrap, FallbackTable.
- Assets/Scripts/UI/ — DiegeticHud (hand-attached, world space, within 30 degrees of view center), WaveCounter, ScoreDisplay, UpgradeChoice, EndScreen.
- Assets/Scripts/Editor/ — SceneBuilder with [MenuItem("HoldTheTable/Build Scene")], Validator with [MenuItem("HoldTheTable/Validate")].
- Assets/Editor/BuildScript.cs is the ONLY BuildScript class; include [MenuItem("HoldTheTable/Build APK")] and public static BuildApk(). It must support: Unity -quit -batchmode -projectPath . -executeMethod BuildScript.BuildApk -logFile build.log
- SceneBuilder creates: XR Origin with hand tracking, MRUK bootstrap, TableAnchorBootstrap with 3s timeout to FallbackTable, Imperial Scroll and civilization setup UI, Town Hall placement token, banner prefab, tray with 3 TraySlots, battlefield spawn boundary, DiegeticHud, QuartermasterAgent and GameDirector wiring. Instantiate the actual Town Hall only after the player's placement confirmation.
- Validator asserts every required component exists and every required prefab is assigned, and fails loudly with a list of what is missing.

## GAMEPLAY SPEC (implement all, no exceptions)
- Placement: after founding, pinch a tray token, move, release over a valid surface inside the connected settlement build area. Invalid placement shows red ghost, valid shows green. Units snap to the selected surface, max 12 defensive structures excluding the Town Hall and banner.
- Units: Wall (cheap, blocks), Archer Tower (damages in radius), Barricade (slows). Each with cost, HP, and a distinct silhouette built from primitives.
- Resource: "Timber", starts 40, +6 per second, towers cost 15, walls 10, barricades 8.
- Waves: 3 waves, counts 6 / 10 / 16, third wave includes a slow "Ram" with 4x HP. 12-second build phase between waves with a visible countdown.
- Shockwave: both palms swept outward within 0.5s, 2 charges total, damages and pushes all monsters within 0.6m, plays a ring VFX.
- Quartermaster: small NPC on the table edge, appears after each wave, offers two upgrade cards chosen from the units the player used most, each card applies a real modifier (damage, HP, cost, shockwave charge). Its lines are 1 short sentence. Fully skippable by doing nothing for 6 seconds — the game must be completable ignoring it.
- Loss: keep HP 0, EndScreen with score and a single big RESTART button reachable by poke.
- Win: wave 3 cleared, EndScreen with score.
- Score: kills * 10 + remaining keep HP * 25 + timber left.
- Fallback: if MRUK finds no plane in 3s, spawn a procedural virtual table 0.45m in front at seat height and run the identical game. This path must be exercised and must not differ in gameplay.
- FoV-aware: combat HUD anchored to the non-dominant wrist, never wider than 30 degrees from center, scales with device FoV. The Imperial Scroll is a separate movable world-space interface beside the player.

## DELIVERABLES BESIDES CODE
- CREDITS.md — every SDK and asset used, with license.
- CHANGELOG.md — factual bullet list of features added during this session. Describe a pre-existing base only if repository history or supplied evidence proves it exists. Never invent project history or eligibility for the Adapted / Significantly Updated division.
- SUBMISSION.md — 140-char tagline, 480-word English description covering inspiration, build, future plans, and the disclosure line: base samples, licenses, and what was added.
- SHOTLIST.md — 8 beats, 0:00 to 2:50, for the demo video.

## EXECUTION ORDER (gates in order, do not reorder)
1. Packages, XR/Android/IL2CPP settings, empty scene with XR Origin. Gate: project compiles, APK builds.
2. MRUK discovery, plane marker, pinch grab of one cube. Gate: cube places on a real plane.
3. Spline path, one monster walking to the keep, keep HP and damage.
4. Full tray, 3 unit types, resource economy, placement validation.
5. 3 waves, ram, win/lose, restart, score.
6. Shockwave, Quartermaster, upgrades.
7. HUD, FoV clamp, VFX, procedural audio via AudioSource + generated clips.
8. Fallback table path. Gate: game playable with MRUK disabled.
9. Perf pass. Gate: 60 fps with 30 units, APK under 150 MB.
10. CREDITS.md, CHANGELOG.md, SUBMISSION.md, SHOTLIST.md. Gate: Validator passes clean.

## DEFINITION OF DONE — all must be true
- APK installs and launches on the connected Quest 3S with hand tracking on; Quest 3 remains a supported target.
- Entire loop completable with hands only, including restart.
- Both MRUK and fallback paths reach the EndScreen.
- Zero console errors and zero missing references at runtime.
- 60 fps sustained in the 30-unit stress test.
- Validator passes. All four markdown files exist and are non-empty.


## Уточнения для исполнения

- Установленные совместимые Unity LTS и Meta SDK имеют приоритет перед ненужным обновлением. Для пустого проекта исходная целевая версия — Unity 2022.3 LTS; проверь совместимость реально доступных пакетов. Записывай точные разрешённые версии, не выдумывай их.
- HUD прикреплён к недоминантной руке; при её выходе из комфортной области используй мягкое перемещение/скрытие и доступный hands-only способ вернуть HUD. Удерживать весь HUD постоянно на видимом запястье и одновременно в центре взгляда геометрически невозможно. Проверяй угол по фактической XR-камере, не по выдуманному FoV устройства.
- Геометрия стола: проверка по реальному контуру плоскости, исключение обнаруженных дыр/разрывов, spawn points по допустимым краям; spline и steering не должны вести юнитов через выявленные пустоты. Fallback использует тот же интерфейс поверхности и весь тот же gameplay.
- Spatial anchor failure не должен останавливать игру: стабильный session-local transform продолжает сессию, а непрошедшая проверка anchor API честно записывается в отчёт.
- При потере hand tracking не размещай токен случайно; pause/resume и восстановление рук должны сохранять состояние игры.
- Гейт реального pinch/place и физического hands-only прохождения не доказывается компиляцией или автоматическим вызовом игрового метода. Если требуется действие пользователя в шлеме, запроси конкретное короткое действие и продолжай независимую работу.

## Источники и подача

[Страница соревнования](https://start-developer-competition-26.devpost.com/) проверена 18 ноября 2026 года. Она требует hands-first взаимодействия, играбельной сборки и видео короче трёх минут. Для Unity указан APK в release channel `Competition` и invite URL для судей. Локально собранный APK сам по себе не является отправленной заявкой. Публикация и отправка заявки — отдельное действие пользователя.

Division `Adapted / Significantly Updated` требует проекта, существовавшего до 24 сентября 2026 года. Без подтверждения не заявляй такую историю; используй фактическую историю проекта при подготовке `SUBMISSION.md`.

Техническая проверка выполнена через `metavr docs search`:
- [Meta Interaction SDK](https://developers.meta.com/vr/documentation/unity/unity-isdk-interaction-sdk-overview)
- [Interaction SDK с Unity XR](https://developers.meta.com/vr/documentation/unity/unity-isdk-getting-started-unityxr)

Перед написанием интеграции сверяй сигнатуры с исходниками установленного пакета и соответствующей документацией. Эти инструкции описывают требуемый результат, а не гарантируют совместимость произвольных версий SDK.
