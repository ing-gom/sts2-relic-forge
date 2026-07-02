# Relic Forge — 접두사 가이드 / Prefix Guide

강화된 유물에 붙는 모든 접두사를 정리했습니다. 접두사는 **런 시드로 결정**되며(같은 시드 = 같은 결과), 시작 유물은 자동 제외됩니다. 기본적으로 유물의 약 **40%만** 접두사를 받습니다(ModConfig 슬라이더로 조절).

*Every prefix a forged relic can roll. Prefixes are **seed-locked** (same seed → same result); starter relics are excluded. By default only ~**40%** of relics get a prefix (tunable via a ModConfig slider).*

> 확률(%)은 **접두사가 붙을 때** 각 접두사가 뽑힐 비중입니다. 실제 유물당 확률 ≈ 40% × 아래 값.
> *The % is each prefix's share **of the prefix pool** (rolled when a relic does get a prefix). Actual per-relic chance ≈ 40% × the value below.*

---

## 1. 수치 접두사 / Numeric prefixes

유물의 수치 효과를 올리거나 내립니다. 툴팁에 증감이 색으로 표시됩니다([green]증가[/green] / [red]감소[/red]).
*Scale a relic's numbers up or down; the tooltip shows the change in color.*

| 접두사 (KO) | Prefix (EN) | 효과 / Effect | 비중 / Share |
|---|---|---|---:|
| 전설적인 | Legendary | +60% | 1.2% |
| 신성한 | Godly | +35% | 2.4% |
| 악마의 | Demonic | +25% | 3.6% |
| 훌륭한 | Superior | +18% | 5.4% |
| 강력한 | Forceful | +12% | 7.2% |
| 고통스러운 | Hurtful | +8% | 9.0% |
| 열성적인 | Zealous | +6% | 9.0% |
| 날카로운 | Keen | +4% | 9.0% |
| 불안정한 | Volatile | 이로운 값·해로운 값 모두 상승 (고위험/고보상) / raises boons **and** downsides | 3.6% |
| 금이 간 | Damaged | −12% | 8.4% |
| 하찮은 | Shoddy | −18% | 4.8% |
| 부서진 | Broken | −25% | 3.0% |

유물별 정확한 강화 수치는 인터랙티브 대시보드 [`prefix_dashboard.html`](prefix_dashboard.html)에서 확인하세요.
*For exact per-relic numbers, open the interactive [`prefix_dashboard.html`](prefix_dashboard.html).*

---

## 2. 동반 유물 접두사 / Companion prefixes

수치를 바꾸는 대신 **다른 유물의 효과를 통째로 부여**합니다. 능력치 유무와 무관하게 **어떤 유물에나** 붙을 수 있습니다. 효과는 host 유물의 툴팁에 표시되고, 발동 시 host 아이콘이 반짝입니다.
*Instead of scaling numbers, these **graft another relic's whole effect** onto yours. They can roll on **any** relic. The effect shows on the host relic's tooltip and flashes the host icon when it triggers.*

| 접두사 (KO) | Prefix (EN) | 효과 / Effect | 원본 유물 / From | 비중 / Share |
|---|---|---|---|---:|
| 가시돋친 | Thorned | 전투 시작 시 가시 3 / Thorns 3 at combat start | 청동 비늘 / Bronze Scales | 5.4% |
| 강건한 | Mighty | 전투 시작 시 힘 +1 / Strength +1 at combat start | 금강저 / Vajra | 3.6% |
| 수은의 | Quicksilver | 매 턴 모든 적에게 3 피해 / 3 dmg to all enemies each turn | 수은 모래시계 / Mercury Hourglass | 3.6% |
| 닻내린 | Anchored | 전투 시작 시 블록 10 / Block 10 at combat start | 닻 / Anchor | 4.2% |
| 피끓는 | Vital | 첫 턴에 체력 2 회복 / Heal 2 on turn 1 | 피가 담긴 병 / Blood Vial | 4.8% |
| 규칙적인 | Rhythmic | 3턴마다 에너지 +1 / +1 energy every 3 turns | 행복한 꽃 / Happy Flower | 3.0% |
| 통찰의 | Insightful | 전투 중 첫 피격 시 카드 3장 드로우 / Draw 3 when first hit | 백년퍼즐 / Centennial Puzzle | 4.2% |
| 위협적인 | Intimidating | 전투 시작 시 모든 적에게 취약 1 / Vulnerable 1 to all at combat start | 구슬 주머니 / Bag of Marbles | 4.8% |

> 동반 접두사는 계속 추가될 예정입니다. / *More companion prefixes are on the way.*

---

## 참고 / Notes

- **밸런스 모드 아님** — 유물을 더 강하게 만드는 파워 판타지/캐주얼용. / *Not a balance mod — a power-fantasy add-on.*
- **저장/로드 유지** — 시드로 재유도되므로 세이브 후에도 동일. / *Survives save/load (re-derived from the seed).*
- **모드 유물 지원** — 수치 접두사는 수치가 있는 모드 유물에 자동 적용. / *Numeric prefixes work on modded relics automatically.*
- **비활성화** — ModConfig "접두사 미적용 확률"을 100%로. / *Disable via the ModConfig "No-prefix chance" slider (100%).*

---

*[Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3755793010) · Built on Slay the Spire 2 by MegaCrit · MIT*
