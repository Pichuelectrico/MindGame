# F1 — Auditoría Mixamo (2026-10-14)

## 1. Rig / Importer
- Todos los 36 `.fbx.meta` en `Assets/Art/Characters/` tienen `animationType: 2` = **Humanoid**. OK.
- `avatarSetup: 0` = Create From This Model. Unity genera 1 Avatar por FBX; para retarget basta con que cada clip Humanoid apunte a cualquier avatar humanoide (Mutant o Y Bot). Recomendación: en Animator usar Avatar de Mutant.fbx para BOSS y de Y Bot.fbx para PLAYER; los clips retargetean entre ambos sin cambios.
- Sin errores de import detectados en metas.

## 2. Clasificación skin vs anim-only (verificado por bytes: presencia de Geometry/Deformer)
- **CON SKIN (2):** `Mutant.fbx` (19.8 MB), `Y Bot.fbx` (1.98 MB). Son los únicos modelos renderizables.
- **SOLO ANIMACIÓN (34 = 32 + 2 bloques):** resto, sin Geometry ni Deformer. Lista:
  Idle, Idle fight, Running, Crouched Walking, Mutant Walking, Mutant Run, Jump, Jump Over, Running Slide, Quick Roll To Run, Front Flip, Front Twist Flip, Dodging, Stunned, Stand Up, Getting Hit, Punching, Combo Punch, Flying Knee Punch Combo, Mutant Punch, Mutant Idle, Mutant Jump Attack, Military Signaling, Frisbee Throw (ThrowWeapon), Sword And Shield Attack, Sword And Shield Slash, Sword And Shield Slash (1), Sword And Shield Kick, Sword And Shield Jump, Sword And Shield Power Up, Sword And Shield Death, One Hand Sword Combo, Left Block, Center Block.

## 3. Retarget
- Todos los clips anim-only son esqueleto Mixamo estándar (sin skin) + Humanoid → retargetean sobre Mutant y Y Bot.
- Verificación en editor pendiente (Play Mode F1-Sandbox), pero a nivel de import no hay bloqueante.

## 4. Convención Assets/Animations/ (carpetas creadas)
- `Assets/Animations/Boss/` — reservado BOSS: `Mutant_Idle`, `Mutant_Walking`, `Mutant_Run`, `Mutant_Punch`, `Mutant_JumpAttack`, `FrontTwistFlip` (solo boss), `MilitarySignaling_PreCombatIdle`.
- `Assets/Animations/Shared/` — locomoción + combate base + evasión + reacciones: `Idle`, `IdleFight`, `Run`, `CrouchedWalk`, `Jump`, `JumpOver`, `RunningSlide`, `QuickRollToRun`, `FrontFlip_EvadeAlt`, `Dodge`, `Stunned`, `StandUp`, `GettingHit_HitReact`, `Punch`, `ComboPunch`, `FlyingKneeCombo`, `ThrowWeapon_FrisbeeThrow`, `SwordAndShieldDeath`.
- `Assets/Animations/Sword/` — quien tenga espada equipada: `SwordShield_Attack`, `SwordShield_Slash`, `SwordShield_Slash2`, `SwordShield_Kick`, `SwordShield_Jump`, `SwordShield_PowerUp`, `OneHandSword_Combo`.
- `Assets/Animations/Shared/` también: `Block_Left`, `Block_Center` (bloqueo direccional).
- Renombrado: al extraer clips (`@`-clips o duplicados), usar PascalCase sin espacios ni paréntesis: `Sword And Shield Slash (1)` → `SwordShield_Slash2`.
- Reglas de diseño aplicadas: Military Signaling = idle pre-combate BOSS; Front Flip/Front Twist Flip = dodge alternativo; Mutant* + FrontTwistFlip solo BOSS; espada = quien la tenga equipada.

## 5. Acción requerida (editor, no automatizable por CLI)
1. Seleccionar cada anim-only → Rig = Humanoid / Avatar Definition = Create From This Model (ya está) → Apply.
2. Extraer/duplicar AnimationClips a `Assets/Animations/<subcarpeta>/` con los nombres de §4.
3. En Sandbox, asignar Avatar Mutant a Mutant y Avatar Y Bot a Y Bot; probar clip de cada carpeta.

## 6. Detrend de hips XZ (F5-hotfix3, 2026-10-14)
- Los 34 `.anim` tenían deriva lineal de cadera horneada (ej: Run +3.59m, Roll +5.56m) que causaba
  snap-back/teleport al cambiar de acción. Se eliminó la deriva (detrend lineal, bob conservado, Y intacto).
- RECUPERACIÓN: los FBX originales están intactos; borrar un `.anim` y re-ejecutar
  `MindGame/Setup/Extract Mixamo Clips` (idempotente) lo restaura con su deriva original.
  Copia de seguridad pre-detrend (34 archivos): `~/MindGameAnimBackups/{Shared,Sword,Boss}/`.
  Verificación: menú `MindGame/Diagnostics/Report Root Motion`.
- NOTA: AttackSword lleva travelDistance 3.48m (embestida larga) — bajar si el playtest lo reporta raro.
