# Dependency notes, unresolved identities and architecture gaps

GENERATED from `evidence/machines/*.json`. Do not hand-edit.

## Multi-output recipes — architecture gap

### Uranium Reprocessing — Nuclear Reprocessor

Inputs: SpentFuel `10`; Ice `5`; Sulfur `2`; PotassiumNitrate `3`

| Output | Qty | Identity |
|---|---:|---|
| Nuclear Fuel | 3 | **unresolved** |
| Depleted Uranium | 7 | **unresolved** |
| Nuclear Waste | 10 | **unresolved** |

ARCHITECTURE GAP: three simultaneous outputs. IOPM models one blueprint -> one product, so this cannot be represented without coproduct accounting. Do NOT force it into the one-output model.

IOPM models one blueprint → one product. Representing a coproduct recipe needs
multi-output accounting **and** a planning policy that does not re-run the recipe
because only one of its outputs was counted against demand. Until both exist,
these are recorded and deliberately not modelled.

## Unresolved and conflicting identities

| Output | Machine | Status |
|---|---|---|
| MR-8P Rifle Magazine | Advanced Assembler | CONFLICT - historical subtype says 5rd, live UI shows capacity 8 |
| S-20A Pistol Magazine | Advanced Assembler | UNRESOLVED - the alias table holds only S-10, S-10E and S-10A pistol magazines; no S-20A entry exists anywhere in the repo |
| Acid Power Cell | Assembler | UNRESOLVED - blueprint id AcidPowerCell is in the repo knowledge table but no physical TypeId/SubtypeId has ever been observed |
| MR-8P Rifle Magazine | Assembler | CONFLICT - the repo's historical subtype ends _5rd; the live UI shows capacity 8. Capacity alone is NOT proof of a different subtype and no replacement has been inferred. |
| Alkaline Power Cell | Fabricator | UNRESOLVED - blueprint id AlkalinePowerCell is in the repo knowledge table but no physical identity has been observed |
| Grinder | Fabricator | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Hand Drill | Fabricator | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Hydrogen Bottle | Fabricator | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Oxygen Bottle | Fabricator | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Basic Pistol | Fabricator | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Welder | Fabricator | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Basic Pistol Magazine | Fabricator | UNRESOLVED - the live UI exposes variants the repo cannot tell apart. Candidate alias(es) in the repo: SemiAutoPistolMagazine (S-10), ElitePistolMagazine (S-10E), FullAutoPistolMagazine (S-10A). A 1:1 mapping is NOT proven and was not inferred. |
| Cooked Mammal Meat | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Cooked Insect Meat | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Meal Pack (Kelp Crisp) | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Meal Pack (Fruit Bar) | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Meal Pack (Garden Slaw) | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Meal Pack (Red Pellets) | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Meal Pack (Chili) | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Meal Pack (Flatbread) | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Meal Pack (Ramen) | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Meal Pack (Fruit Pastry) | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Meal Pack (Veggie Burger) | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Meal Pack (Curry) | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Meal Pack (Green Pellets) | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Meal Pack (Dumplings) | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Meal Pack (Spaghetti) | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Meal Pack (Lasagna) | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Meal Pack (Burrito) | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Meal Pack (Frontier Stew) | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Meal Pack (Seared Sabiroid) | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Meal Pack (Steak Dinner) | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Harvest Fruit Seeds | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Harvest Grain Seeds | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Harvest Vegetable Seeds | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Harvest Mushroom Spores | Food Processor | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| AP Autocannon Clip | Munitions Factory | UNRESOLVED - the live UI exposes variants the repo cannot tell apart. Candidate alias(es) in the repo: AutocannonMagazine -> AutocannonClip (single alias, two live variants AP/DU). A 1:1 mapping is NOT proven and was not inferred. |
| DU Autocannon Clip | Munitions Factory | UNRESOLVED - the live UI exposes variants the repo cannot tell apart. Candidate alias(es) in the repo: AutocannonMagazine -> AutocannonClip (single alias, two live variants AP/DU). A 1:1 mapping is NOT proven and was not inferred. |
| DU Gatling Ammo Box | Munitions Factory | UNRESOLVED - the live UI exposes variants the repo cannot tell apart. Candidate alias(es) in the repo: GatlingAmmo -> NATO_25x184mm (single alias, two live variants plain/DU). A 1:1 mapping is NOT proven and was not inferred. |
| AP Artillery Shell | Munitions Factory | UNRESOLVED - the live UI exposes variants the repo cannot tell apart. Candidate alias(es) in the repo: ArtilleryShell -> LargeCalibreAmmo (single alias, three live variants AP/DUAP/HE). A 1:1 mapping is NOT proven and was not inferred. |
| DUAP Artillery Shell | Munitions Factory | UNRESOLVED - the live UI exposes variants the repo cannot tell apart. Candidate alias(es) in the repo: ArtilleryShell -> LargeCalibreAmmo (single alias, three live variants). A 1:1 mapping is NOT proven and was not inferred. |
| HE Artillery Shell | Munitions Factory | UNRESOLVED - the live UI exposes variants the repo cannot tell apart. Candidate alias(es) in the repo: ArtilleryShell -> LargeCalibreAmmo (single alias, three live variants). A 1:1 mapping is NOT proven and was not inferred. |
| Large AP Railgun Sabot | Munitions Factory | UNRESOLVED - the live UI exposes variants the repo cannot tell apart. Candidate alias(es) in the repo: LargeRailgunSabot -> LargeRailgunAmmo (single alias, AP/DUAP live variants). A 1:1 mapping is NOT proven and was not inferred. |
| Large DUAP Railgun Sabot | Munitions Factory | UNRESOLVED - the live UI exposes variants the repo cannot tell apart. Candidate alias(es) in the repo: LargeRailgunSabot -> LargeRailgunAmmo (single alias, AP/DUAP live variants). A 1:1 mapping is NOT proven and was not inferred. |
| AP Assault Cannon Shell | Munitions Factory | UNRESOLVED - the live UI exposes variants the repo cannot tell apart. Candidate alias(es) in the repo: AssaultCannonShell -> MediumCalibreAmmo and the live-observed MediumCalibreAmmoHE; which variant maps to which is NOT proven. A 1:1 mapping is NOT proven and was not inferred. |
| DUAP Assault Cannon Shell | Munitions Factory | UNRESOLVED - the live UI exposes variants the repo cannot tell apart. Candidate alias(es) in the repo: AssaultCannonShell -> MediumCalibreAmmo / MediumCalibreAmmoHE. A 1:1 mapping is NOT proven and was not inferred. |
| HE Assault Cannon Shell | Munitions Factory | UNRESOLVED - the live UI exposes variants the repo cannot tell apart. Candidate alias(es) in the repo: AssaultCannonShell -> MediumCalibreAmmo / MediumCalibreAmmoHE. A 1:1 mapping is NOT proven and was not inferred. |
| HE Rocket | Munitions Factory | UNRESOLVED - the live UI exposes variants the repo cannot tell apart. Candidate alias(es) in the repo: Missile -> Missile200mm (single alias, live Rocket and HE Rocket variants). A 1:1 mapping is NOT proven and was not inferred. |
| Light Turret Box | Munitions Factory | UNRESOLVED - the live UI exposes variants the repo cannot tell apart. Candidate alias(es) in the repo: InteriorTurret_Mag_50rd is a live-observed subtype whose 50-round capacity matches, but the mapping is not proven; UI result text also read "Small Turret Box". A 1:1 mapping is NOT proven and was not inferred. |
| Rocket | Munitions Factory | UNRESOLVED - the live UI exposes variants the repo cannot tell apart. Candidate alias(es) in the repo: Missile -> Missile200mm (single alias, live Rocket and HE Rocket variants). A 1:1 mapping is NOT proven and was not inferred. |
| Gatling Ammo Box | Munitions Factory | UNRESOLVED - the live UI exposes variants the repo cannot tell apart. Candidate alias(es) in the repo: GatlingAmmo -> NATO_25x184mm (single alias, plain/DU live variants). A 1:1 mapping is NOT proven and was not inferred. |
| Small AP Railgun Sabot | Munitions Factory | UNRESOLVED - the live UI exposes variants the repo cannot tell apart. Candidate alias(es) in the repo: SmallRailgunSabot -> SmallRailgunAmmo (single alias, AP/DUAP live variants). A 1:1 mapping is NOT proven and was not inferred. |
| Small DUAP Railgun Sabot | Munitions Factory | UNRESOLVED - the live UI exposes variants the repo cannot tell apart. Candidate alias(es) in the repo: SmallRailgunSabot -> SmallRailgunAmmo (single alias, AP/DUAP live variants). A 1:1 mapping is NOT proven and was not inferred. |
| S-20A Pistol Magazine | Munitions Factory | UNRESOLVED - the repo holds only S-10, S-10E and S-10A pistol magazines. No S-20A entry exists anywhere; nothing was inferred from the display name. |
| MR-8P Rifle Magazine | Munitions Factory | CONFLICT - the repo subtype ends _5rd; the live UI shows capacity 8. Capacity alone is NOT proof of a different subtype and no replacement has been inferred. |
| Basic Pistol Magazine | Munitions Factory | UNRESOLVED - the live UI exposes variants the repo cannot tell apart. Candidate alias(es) in the repo: SemiAutoPistolMagazine / ElitePistolMagazine / FullAutoPistolMagazine. A 1:1 mapping is NOT proven and was not inferred. |
| PRO-1 Rocket Launcher | Nano-Assembler | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Elite Grinder | Nano-Assembler | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| S-10E Pistol | Nano-Assembler | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Elite Hand Drill | Nano-Assembler | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| MR-30E Rifle | Nano-Assembler | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Elite Welder | Nano-Assembler | UNRESOLVED - no physical TypeId/SubtypeId has ever been observed for this product |
| Uranium Reprocessing | Nuclear Reprocessor | UNRESOLVED - SpentFuel is live-observed as MyObjectBuilder_Ingot/SpentFuel, but Nuclear Fuel and Nuclear Waste identities were NOT inferred from display names |
| Composite Plate | Plate Stamp | UNRESOLVED - the repo holds a blueprint id under the historical name CompositeArmor. Equality with the live "Composite Plate" is NOT proven and was not assumed. |
| Asphalt | Synthetics Factory | UNRESOLVED - blueprint id Asphalt is in the repo knowledge table but no physical TypeId/SubtypeId has ever been observed |

**Nothing above was resolved by inference.** A physical subtype is never
derived from a display name, and a capacity shown in the UI is not proof of a
different subtype.
