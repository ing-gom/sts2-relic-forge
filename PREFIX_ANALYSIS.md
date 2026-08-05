# RelicForge 신규 접두사 — 구현 가능성 분석 (v1.0.41 대상)

> 생성 2026-07-22. 입력=[PREFIX_CANDIDATES.md](PREFIX_CANDIDATES.md)(밸런스 정리 후 활성 90종).
> 목적: "**게임에 필요한 훅/API가 실제로 있는가**"를 코드로 검증해 구현 가능/난이도/코옵을 확정하고, v1.0.41 배치를 추천.
> 근거: 게임 Hook 표면(디컴파일 실측)+DamageResult 필드+기존 RelicForge 패치가 이미 쓰는 훅 21종.

---

## 헤드라인 결론

**후보의 병목은 "게임에 훅이 없어서"가 아니었다.** 실측한 Hook 표면은 100+ 훅이고,
`DamageResult` 구조체가 결정적 필드를 이미 노출한다:

```
DamageResult { UnblockedDamage, BlockedDamage, OverkillDamage, WasFullyBlocked, WasTargetKilled, ... }
```

→ 내가 "완전 미개척"이라 표시했던 축들이 **계산 없이 필드 하나로** 구현된다:
- **초과피해(F1)** = `DamageResult.OverkillDamage` (직접 노출)
- **완전방어(F3)** = `DamageResult.WasFullyBlocked` (직접 노출)
- **손패 비움(F4)** = 전용 훅 `AfterHandEmptied(ctx, player)`
- **셔플(F11)** = 전용 훅 `AfterShuffle(ctx, shuffler)`
- **회복 반응(F9)** = `AfterCurrentHpChanged(creature, delta)` (delta>0=회복)
- **카드 코스트(F5)** = `ModifyEnergyCostInCombat(cs, card, cost)` (순수 계산 훅=최안전)
- **에너지/블록 턴종료(F10/F2/F20)** = `AfterTurnEnd(ctx, side)`
- **막 깊이(F16)** = `AfterActEntered()` + RunState
- **상점/휴식/이벤트(F17)** = `AfterItemPurchased` / `AfterRestSiteHeal` / `ModifyRestSiteHealAmount` / `AfterEventStarted`
- **연승/속공(F6/F18)** = `AfterCombatVictory(room)` (단, 전투-간 영속은 여전히 직렬화 필요)

**즉 90종 중 ~80종이 "Ready"(훅 존재).** 진짜 제약은 훅이 아니라 (1) co-op 클래스, (2) 전투-간 직렬화([C]) 뿐.

---

## 검증된 훅 매핑 (핵심 후보)

| # | 후보 | 사용 훅 (실측 시그니처) | co-op | 난이도 | 판정 |
|---|---|---|:---:|:---:|:---:|
| 4 | Cleaving | `AfterDamageGiven(ctx,dealer,result,…,target)` → `result.WasTargetKilled && OverkillDamage>0` | [A] | 낮음 | ✅ Ready |
| 14 | Vindictive(하향) | `AfterDamageReceived` → `result.WasFullyBlocked` (EnemyReactive 패턴 재사용) | [A] | 낮음 | ✅ Ready |
| 38 | Detonating | `AfterDeath` (ContagionKillPatch 구조 재사용) — 죽은 적 Poison → 즉발 피해 | [A] | 낮음 | ✅ Ready |
| 45 | Transfusion | `AfterCurrentHpChanged(creature, delta)` delta>0 → 블록 delta/2 | [A] | 낮음 | ✅ Ready |
| 18 | Emptyhanded | `AfterHandEmptied(ctx, player)` → 다음 턴 에너지 (전용 훅!) | [A] | 낮음 | ✅ Ready |
| 52 | Frugal | `AfterTurnEnd(ctx, side)` player측 → 안 쓴 에너지 read → 다음 턴 블록 | [A] | 중간 | ✅ Ready |
| 81 | Seasoned | 전투시작 dispatch(기존) + `RunState`의 Act 번호 | [A] | 낮음 | ✅ Ready |
| 22 | Tempo | `ModifyEnergyCostInCombat(cs, card, cost)` 첫 카드 비용 −1 | [A] | 중간 | ✅ Ready |
| 67 | Vanguard | 전투시작(기존) + `cs.HittableEnemies.Count==1` | [A] | 낮음 | ✅ Ready |
| 31 | Blitz | `AfterDeath` + `TurnNumber<=3` | [A] | 낮음 | ✅ Ready |
| 13 | Backlash | `AfterBlockBroken(creature)` → 무작위 적 피해 | [A] | 낮음 | ✅ Ready |
| 10 | Turtling | `AfterBlockGained(creature,amt,…)` → block>=30 once → Str | [A] | 낮음 | ✅ Ready |
| 84 | Merchant's Favor | `AfterItemPurchased(player, entry, goldSpent)` → 골드 환급 | **[B]** | 중간 | ✅ Ready(coop-verify) |
| 86 | Restful | `ModifyRestSiteHealAmount` / `AfterRestSiteHeal` | **[B]** | 낮음 | ✅ Ready |
| 89 | Wanderer | `AfterEventStarted` → 골드 | **[B]** | 낮음 | ✅ Ready |
| 91~95 | 연승/영속 | `AfterCombatVictory(room)` + **ForgeRecord per-run 카운터 신설** | **[C]** | 높음 | ⚠️ 직렬화 설계 |

> [A]=적/전투상태 결정적 양피어 or 순수 계산훅(ModifyEnergyCostInCombat=ModifyDamage와 같은 계산 클래스, 상태 무변경=최안전). [B]=골드(IsMe+sync). [C]=전투-간 영속(세이브 재유도+피어 동기).

---

## 훅 레버리지 (신규 훅 1개 = 후보 N개 해금)

새 패치 파일 하나가 여러 후보를 동시에 연다 — 배치 구현의 근거:

| 신규 훅 패치 | 해금 후보 |
|---|---|
| `AfterTurnEnd` 디스패치 | 6 Bulwark Oath · 11 Overguard · 18(부분) · **52 Frugal** · 49 Combustion · 50 Meditation · 51 Static · 100 Bramble |
| `ModifyEnergyCostInCombat` | **22 Tempo** · 24 Overload Cost · 25 Kindling · 27 Miser |
| `AfterCurrentHpChanged` | **45 Transfusion** · 44 Convalescent · 46 Overheal · 47 Vitality · 70 Sanguine · 72 Hemocraft |
| `AfterDamageGiven`(오버킬) | **4 Cleaving** · 65 Ricochet · 68 Scattershot |
| `AfterHandEmptied` | **18 Emptyhanded** · 61 Closer |
| `AfterShuffle` | **54 Reshuffle** · 57 Recycler |
| `AfterBlockGained/Broken` | **10 Turtling** · 13 Backlash · 6 Bulwark Oath |
| `AfterCombatVictory` | 91~95 연승/속공 계열 (전부 [C]) |

---

## ★ 추천 v1.0.41 배치 (8종, 전부 [A])

선정 기준: **훅 검증됨 + 각각 새 메커니즘 축 + co-op [A] + 밸런스 통과 + 기존과 비중복**.
전염의(v1.0.40)에 이어 "**처치·방어·자원 반응**"이라는 신규 손맛 축을 한 번에 연다.

| # | 이름 | 효과 | 여는 축 | 훅 |
|---|---|---|---|---|
| 4 | **Cleaving (참격의)** | 처치 시 초과 피해를 무작위 다른 적에게 | 초과피해 | AfterDamageGiven |
| 14 | **Vindictive (앙갚음의)** | 완전히 막으면 절반(≤12)을 공격자에게 반사 | 완전방어 | AfterDamageReceived |
| 38 | **Detonating (기폭의)** | 적 처치 시 남은 중독을 즉발 피해로 | 디버프 기폭 | AfterDeath |
| 45 | **Transfusion (수혈의)** | HP 회복 시 절반만큼 방어도 | 회복 반응 | AfterCurrentHpChanged |
| 18 | **Emptyhanded (빈손의)** | 손패 비우고 턴 마치면 다음 턴 에너지 | 손패 크기 | AfterHandEmptied |
| 52 | **Frugal (절약의)** | 안 쓴 에너지 1당 다음 턴 방어도 3 | 미사용 에너지 | AfterTurnEnd |
| 81 | **Seasoned (숙성의)** | 현재 막 번호만큼 시작 힘·방어도(상한 有) | 막 깊이 | 전투시작(기존) |
| 22 | **Tempo (박자의)** | 매 턴 첫 카드 비용 −1 | 카드 코스트 | ModifyEnergyCostInCombat |

**왜 이 8종**: 신규 훅 4개(AfterDamageGiven·AfterCurrentHpChanged·AfterHandEmptied·AfterTurnEnd·ModifyEnergyCostInCombat)만 추가하면 되고, 각 훅이 **후속 후보 다수를 해금**(위 레버리지 표)해 다음 배치가 싸진다. Detonating은 **ContagionKillPatch 구조를 그대로 재사용**(중독 버전)이라 거의 공짜 + 방금 배포한 전염의와 테마 짝. 전부 solo-verify만으로 검증 가능([A]).

> 대안 축소판(4종, 신규 훅 2개): Cleaving · Vindictive · Detonating · Transfusion — 처치/방어/회복만.

---

## 보류 / 추가 연구 필요

| 항목 | 사유 |
|---|---|
| **연승·영속 (91~95)** | [C]. `AfterCombatVictory`는 있으나 **ForgeRecord에 per-run 카운터 신설 + 세이브 재유도 + co-op 피어 동기**가 필요. 별도 릴리스 + coop-verify 필수. |
| **엘리트/보스 게이트 (97~99)** | 조우 **tier**(Normal/Elite/Boss) read 경로 확인 필요(`CombatRoom`/`EncounterModel`에 있을 것으로 추정, 구현 시 1줄 검증). 기능 자체는 [A]. |
| **포션 슬롯 조작 (약제/발효 계열 확장)** | 포션 **획득/슬롯**은 `AfterPotionProcured` 있음. 슬롯 수 증감은 host-authoritative 가능성 → [B], coop-verify. |
| **상점/노드 골드 (84·89·98)** | [B]. `AfterItemPurchased`/`AfterEventStarted` 존재하나 골드는 IsMe+SyncLocal 필요(KillGold 패턴 재사용). |

---

## 다음 단계
1. **v1.0.41 = 추천 8종** 확정 → PrefixTable 확장(var·비중·색·NoteXx 3언어) + 신규 훅 패치 4개 + SoloTest 케이스 8개 → solo-verify → 배포. (전부 [A]라 coop 프로브 불요, 전염의와 동일 판단.)
2. 축소판(4종)으로 갈지, 8종 풀배치로 갈지만 정해주시면 착수합니다.
3. 이후 배치는 위 "훅 레버리지" 표대로 같은 훅을 공유하는 후보들을 묶어 저비용으로 확장.
