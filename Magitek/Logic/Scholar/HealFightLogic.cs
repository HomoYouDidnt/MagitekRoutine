using Buddy.Coroutines;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using Magitek.Extensions;
using Magitek.Models.Account;
using Magitek.Models.QueueSpell;
using Magitek.Models.Scholar;
using Magitek.Utilities;
using System;
using System.Linq;
using System.Threading.Tasks;
using Auras = Magitek.Utilities.Auras;
using ScholarRoutine = Magitek.Utilities.Routines.Scholar;

namespace Magitek.Logic.Scholar
{
    internal static class HealFightLogic
    {
        public static async Task<bool> Aoe()
        {
            // No party gate: fight-logic mitigation should fire solo too (self-target) — e.g. Occult
            // Crescent field ops. Reactions below target Core.Me or fall back to self.
            if (!FightLogic.ZoneHasFightLogic())
                return false;

            if (FightLogic.EnemyIsCastingBigAoe() || FightLogic.EnemyIsCastingAoe())
            {
                if (!FightLogic.HodlCastTimeRemaining(hodlTillDurationInPct: BaseSettings.Instance.FightLogicResponseDelay))
                    return false;

                // Solo: BigAoe()/JustAoe() read Core.Me.CurrentTarget.SpellCastInfo (NRE with no target,
                // and the AoE trigger can be target-independent) and spread Deployment Tactics to a party
                // that isn't there. Out of party, shield ourselves directly instead.
                if (!Globals.InParty)
                {
                    if (ScholarSettings.Instance.FightLogicSoilBigAoe && Spells.SacredSoil.IsKnownAndReady() && Core.Me.HasAetherflow())
                        return await FightLogic.DoAndBuffer(Spells.SacredSoil.Cast(Core.Me));

                    // Both barriers are wasted on someone who already has one — they replace rather than
                    // stack, so the cast buys nothing but a refreshed timer.
                    if (ScholarSettings.Instance.FightLogicAdloDeployBigAoe && Spells.Adloquium.IsKnownAndReady() && !Core.Me.HasPrimaryShield())
                        return await FightLogic.DoAndBuffer(ScholarRoutine.AdloquiumSpell.Cast(Core.Me));

                    if (ScholarSettings.Instance.FightLogicSuccorAoe && Spells.Succor.IsKnownAndReady() && !Core.Me.HasPrimaryShield())
                        return await FightLogic.DoAndBuffer(ScholarRoutine.SuccorSpell.Cast(Core.Me));

                    return false;
                }

                if (await BigAoe())
                    return true;
                if (await JustAoe())
                    return true;

                // Only inside the detection branch: a mechanic is in flight and every barrier above
                // declined. Outside it this would print every 2s for the whole run.
                LogAoeDecline();
            }

            return false;
        }

        // Every barrier above declined. The detector re-fires each pulse until something handles the
        // cast, so this prints at most once every two seconds. Reports the value of every gate so a
        // decline can be read off the log instead of guessed at — a live run left two unanswered
        // raidwides (Empty Proclamation, Super Nova) whose cause could only be reconstructed by
        // inference. Same shape as the Sage diagnostic that identified the IsMainTank defect.
        private static int _lastAoeDeclineLogTick;

        private static void LogAoeDecline()
        {
            if (!BaseSettings.Instance.DebugFightLogic)
                return;

            if (Environment.TickCount - _lastAoeDeclineLogTick <= 2000)
                return;

            _lastAoeDeclineLogTick = Environment.TickCount;

            // succorReady / remainMs / deployReady / the barriered-vs-threshold verdict decided 28 of
            // 43 declines in the audited run and none of them was printable — a decline could name
            // every gate except the ones that actually fired.
            var caster = FightLogic.DetectedCaster();
            var remainMs = caster != null && caster.IsCasting
                ? (int)caster.SpellCastInfo.RemainingCastTime.TotalMilliseconds
                : -1;

            Logger.WriteInfo(
                $"[AOE Declined/SCH] moving={MovementManager.IsMoving} aetherflow={ActionResourceManager.Scholar.Aetherflow} "
                + $"soilReady={Spells.SacredSoil.IsKnownAndReady()} recitReady={Spells.Recitation.IsKnownAndReady()} "
                + $"succorReady={Spells.Succor.IsKnownAndReady()} deployReady={Spells.DeploymentTactics.IsKnownAndReady()} "
                + $"remainMs={remainMs} succorCastMs={(int)Spells.Succor.AdjustedCastTime.TotalMilliseconds} "
                + $"seraphism={Core.Me.HasAura(Auras.Seraphism)} et={Core.Me.HasAura(Auras.EmergencyTactics)} "
                + $"queueLive={SpellQueueLogic.SpellQueue.Any()} "
                + $"barriered={Group.CastableParty.Count(x => x.HasPrimaryShield())}/{AoeThreshold} "
                + $"galvanizeCarrier={Group.CastableParty.Any(x => x.HasAura(Auras.Galvanize, true))}");
        }

        private static async Task<bool> BigAoe()
        {
            // The reaction fires on either an enemy AoE cast or a target-independent AoE lock-on (which has
            // no cast to time against). Handle both: when the boss is mid-cast we size the shield against its
            // remaining cast; on a lock-on there's no cast, so we assume the few-seconds lead the marker
            // gives before its raidwide and still fire a shield. (Previously the method bailed the instant
            // the target wasn't casting, so lock-ons like Alexander's Banishga IV tell got no response.)
            // Time the window against the caster the detector bound, not the player's target — in
            // multi-actor fights (Shinryu's segment ring) the mechanic's caster is routinely not
            // the current target, and a CurrentTarget read measures those windows as zero.
            var enemyTarget = FightLogic.DetectedCaster() ?? Core.Me.CurrentTarget as Character;
            bool enemyCasting = enemyTarget != null && enemyTarget.IsCasting;
            int castTimeRemaining = enemyCasting
                ? (int)enemyTarget.SpellCastInfo.RemainingCastTime.TotalMilliseconds
                : 3000;

            // A cast-time shield (Adloquium / Succor) only helps if we can stand still and finish the cast
            // before the hit. On a short enemy cast (e.g. Banishga IV) or while moving that's impossible, so
            // we fall through to instant Sacred Soil; on a lock-on we can't measure the window, so allow the
            // shield as long as we're not moving.
            bool canCastShield = !MovementManager.IsMoving
                && Spells.Succor.IsKnownAndReady()
                && (!enemyCasting || enemyTarget.SpellCastInfo.RemainingCastTime > Spells.Succor.AdjustedCastTime);

            // Swiftcast rescue for the Recitation carrier combo below. That combo is refused whenever
            // Adloquium cannot be cast — while moving, or when the mechanic lands sooner than its cast
            // time — which accounted for 16 of 60 logged declines across a night of field content.
            // Swiftcast makes that Adloquium instant and turns the decline into a full Deploy.
            //
            // Deliberately narrow, because this spends the healer's resurrection insurance:
            //  - only when the combo would ALREADY be declined, never to shave time off one that lands
            //    anyway (a 60s cooldown is not worth ~1.4s);
            //  - never when the cast is already instant. AdjustedCastTime reflects Dualcast (measured:
            //    it reads 0 while the aura is up, so a phantom Red Mage gets this for free) and
            //    Seraphism's masked instants, and Swiftcast would buy nothing in either case;
            //  - never while a raisable corpse is up, where the raise has the stronger claim on it.
            bool swiftcastRescue = !canCastShield
                && ScholarSettings.Instance.FightLogicSwiftcastDeployCarrier
                && Spells.Swiftcast.IsKnownAndReady()
                && !Core.Me.HasAura(Auras.Swiftcast)
                && ScholarRoutine.AdloquiumSpell.AdjustedCastTime > TimeSpan.Zero
                && !Group.DeadAllies.Any();

            // Instant, castable while moving, no cast window required.
            async Task<bool> TrySacredSoil()
            {
                if (!ScholarSettings.Instance.FightLogicSoilBigAoe
                    || !Spells.SacredSoil.IsKnownAndReady()
                    || !Core.Me.HasAetherflow())
                    return false;

                // Same-effect dedup: Occult Mighty Guard already cut this hit party-wide — Soil's 10%
                // buys almost nothing on top, so save the Aetherflow. Barrier responses above still
                // fire; an absorb complements a cut.
                if (Core.Me.HasAura(Roles.OCAuras.OccultMightyGuard))
                    return false;

                if (BaseSettings.Instance.DebugFightLogic)
                    Logger.WriteInfo($"[AOE Response] Cast Sacred Soil");

                return await FightLogic.DoAndBuffer(Spells.SacredSoil.Cast(Utilities.Routines.Scholar.SacredSoilTarget()));
            }

            // Copying an EXISTING shield is an instant oGCD: no cast window, works while moving — a
            // live audit caught a carrier and a ready Deploy starving behind a rolling GCD. Only the
            // self-BUILD half further down (which hardcasts Adloquium) needs the cast window.
            async Task<bool> TryDeployExistingShield()
            {
                if (!ScholarSettings.Instance.FightLogicAdloDeployBigAoe
                    || !Spells.DeploymentTactics.IsKnownAndReady())
                    return false;

                // Deployment Tactics copies Galvanize only, and its tooltip is explicit: "No effect when
                // target is not under the effect of Galvanize." Catalyze is never carried across — it exists
                // precisely so critical Adloquium shields can't be spread. So Galvanize is the hard
                // requirement, not Catalyze.
                //
                // This matters in practice rather than in theory: when a target holds both, Galvanize is
                // consumed FIRST, so a half-shielded ally routinely ends up with Catalyze and no Galvanize.
                // Selecting on Catalyze alone (as this did) would then deploy onto them for literally no
                // effect. Catalyze is still worth preferring as a tiebreaker — it only comes from a critical
                // Adloquium, whose larger heal also made a larger Galvanize, so that's the better shield to
                // copy.
                // The copy carries only the time left on the original and is not refreshed, so the barrier
                // has to outlast the hit we are answering. Enough to cover the incoming cast is the floor;
                // beyond that prefer one with real duration left rather than burning the cooldown on a
                // barrier that lapses straight after.
                var minimumShieldMs = Math.Max(castTimeRemaining + 1000,
                    ScholarSettings.Instance.DeploymentTacticsMinimumShieldSeconds * 1000);

                var carrier = Group.CastableParty.FirstOrDefault(x => x.HasAura(Auras.Galvanize, true, minimumShieldMs)
                                                                      && x.HasAura(Auras.Catalyze, true, minimumShieldMs));

                if (carrier == null) carrier = Group.CastableParty.FirstOrDefault(x => x.HasAura(Auras.Galvanize, true, minimumShieldMs));

                // Nothing with comfortable duration — fall back to anything that at least survives the hit.
                if (carrier == null) carrier = Group.CastableParty.FirstOrDefault(x => x.HasAura(Auras.Galvanize, true, castTimeRemaining + 1000));

                if (carrier == null)
                    return false;

                if (BaseSettings.Instance.DebugFightLogic)
                    Logger.WriteInfo($"[AOE Response] Cast Deploy Adlo");

                return await FightLogic.DoAndBuffer(Spells.DeploymentTactics.Cast(carrier));
            }

            // The copy outranks Sacred Soil in BOTH branches — it is instant and stronger, per the
            // same doctrine the stationary path has always followed.
            if (await TryDeployExistingShield())
                return true;

            // Can't land a cast-time shield this pulse — we're moving, or the hit arrives sooner than a
            // Succor could finish. Sacred Soil answers instead.
            if (!canCastShield)
            {
                if (await TrySacredSoil())
                    return true;
            }

            // Building a fresh shield to spread means hardcasting Adloquium first — the exact rooted
            // cast the Soil-first preference exists to avoid. Spreading an EXISTING shield above stays
            // ahead of Soil (it's instant and stronger); only this self-build path yields — and only
            // this half needs the cast window (canCastShield), since the copy above is instant.
            if ((canCastShield || swiftcastRescue) &&
                ScholarSettings.Instance.FightLogicAdloDeployBigAoe &&
                Spells.DeploymentTactics.IsKnownAndReady() &&
                !ScholarSettings.Instance.PrioritizeSacredSoilOverSuccor)
            {
                var target = Core.Me;

                // Recitation -> Adloquium -> Deployment Tactics has to land as one unit. Cast inline it
                // could lose the GCD between the first two — Adloquium's cast fails under the animation
                // lock Recitation just started, this method returns false, and the damage rotation slots a
                // Broil in before the shield ever goes out. Queue it instead: the queue is drained ahead
                // of the damage rotation. That alone is NOT a wedge-proof guarantee — oGCD heal logic
                // proved able to fire between arming and the first step (ET landed 0.8s after arming and
                // ate the crit), which is why EmergencyTactics now refuses to cast into a live queue and
                // this path refuses to arm with ET already up. Skipped under Seraphism: Recitation
                // neither applies to nor is consumed by the masked Manifestation, and the inline
                // masked-instant fallback below is strictly better there.
                if (Spells.Recitation.IsKnownAndReady()
                    && !Core.Me.HasAura(Auras.EmergencyTactics)
                    && !Core.Me.HasAura(Auras.Seraphism))
                {
                    SpellQueueLogic.SpellQueueReset(() => SpellQueueLogic.Timeout.ElapsedMilliseconds > 8000);

                    // First in the queue so the Adloquium two entries down is already instant when it
                    // runs. Both this and Recitation are oGCDs, so they weave ahead of that GCD.
                    if (swiftcastRescue)
                        SpellQueueLogic.SpellQueue.Enqueue(new QueueSpell
                        {
                            Spell = Spells.Swiftcast,
                            TargetSelf = true
                        });

                    SpellQueueLogic.SpellQueue.Enqueue(new QueueSpell
                    {
                        Spell = Spells.Recitation,
                        TargetSelf = true
                    });
                    SpellQueueLogic.SpellQueue.Enqueue(new QueueSpell
                    {
                        Spell = Spells.Adloquium,
                        TargetSelf = true,
                        Wait = new QueueSpellWait
                        {
                            Name = "Recitation to apply",
                            WaitTime = 1000,
                            Check = () => Core.Me.HasAura(Auras.Recitation, true)
                        }
                    });
                    SpellQueueLogic.SpellQueue.Enqueue(new QueueSpell
                    {
                        Spell = Spells.DeploymentTactics,
                        TargetSelf = true,
                        Wait = new QueueSpellWait
                        {
                            Name = "Galvanize to apply",
                            WaitTime = 1500,
                            Check = () => Core.Me.HasAura(Auras.Galvanize, true)
                        }
                    });

                    if (BaseSettings.Instance.DebugFightLogic)
                        Logger.WriteInfo($"[AOE Response] Queued Recitation > Adloquium > Deployment Tactics");

                    // The combo executes over the next pulses; mark the mechanic handled NOW so no other
                    // branch answers the same cast in the meantime, and so this branch stops re-queueing
                    // every pulse until the queue engine picks it up.
                    FightLogic.BufferQueuedResponse();

                    return true;
                }

                // Same ET refusal as the queued path above: an ET-consumed Adloquium heals instead
                // of shielding, leaving no Galvanize to deploy. Deliberately NOT routed through
                // DoAndBuffer: leaving the mechanic unlatched lets the next pulse re-detect and
                // finish the job (Deploy on the fresh Galvanize, or Soil) — the cost is a cosmetic
                // decline print while the pair completes. A layered booking here was reviewed and
                // rejected: it denies wave 2 of the same cast id, denies lock-on reactions outright
                // (no cast id to budget against), and Sacred Soil races out the Deploy regardless.
                if (!Core.Me.HasAura(Auras.EmergencyTactics)
                    && await ScholarRoutine.AdloquiumSpell.Cast(target))
                    return await FightLogic.DoAndBuffer(Spells.DeploymentTactics.Cast(target));
                // Adloquium didn't fire — fall through to Sacred Soil / Succor rather than bail the reaction.
            }

            // User preference: answer with the instant Sacred Soil before anything that must hardcast
            // (Recitation > Succor, plain Succor, or building a shield to deploy). Gated on canCastShield
            // because the moving/short-window path above already tried Soil this pulse. Falls through to
            // the shield branches when Soil is on cooldown, unfunded, or not enabled as a fight-logic
            // response (FightLogicSoilBigAoe).
            if (canCastShield && ScholarSettings.Instance.PrioritizeSacredSoilOverSuccor)
            {
                if (await TrySacredSoil())
                    return true;
            }

            if (canCastShield &&
                ScholarSettings.Instance.FightLogicRecitSuccorBigAoe &&
                Spells.Recitation.IsKnownAndReady() &&
                !Core.Me.HasAura(Auras.EmergencyTactics) &&
                // Skipped under Seraphism: Recitation does not interact with the masked Accession,
                // and the plain shield path below casts the masked instant directly.
                !Core.Me.HasAura(Auras.Seraphism) &&
                Group.CastableParty.Count(x => x.HasPrimaryShield()) < AoeThreshold)
            {
                if (BaseSettings.Instance.DebugFightLogic)
                    Logger.WriteInfo($"[AOE Response] Queued Recitation > Succor");

                // Same atomicity problem as the Deploy path above — queue the pair so a Broil can't land
                // between the buff and the shield it is supposed to empower.
                SpellQueueLogic.SpellQueueReset(() => SpellQueueLogic.Timeout.ElapsedMilliseconds > 8000);

                SpellQueueLogic.SpellQueue.Enqueue(new QueueSpell
                {
                    Spell = Spells.Recitation,
                    TargetSelf = true
                });
                SpellQueueLogic.SpellQueue.Enqueue(new QueueSpell
                {
                    Spell = Spells.Succor,
                    TargetSelf = true,
                    Wait = new QueueSpellWait
                    {
                        Name = "Recitation to apply",
                        WaitTime = 1000,
                        Check = () => Core.Me.HasAura(Auras.Recitation, true)
                    }
                });

                // Same as the Deploy path: the pair executes over the next pulses, so mark the mechanic
                // handled at queue time — otherwise this branch re-queues every pulse and another branch
                // can answer the same cast while the queue runs.
                FightLogic.BufferQueuedResponse();

                return true;
            }

            // Plain Succor before falling back to Sacred Soil: a barrier beats a 10% reduction, and with
            // Deployment Tactics and Recitation both down this is the best shield left. Previously Sacred
            // Soil sat above this and returned first, so the shield never got a look in.
            //
            // Kept ahead of Soil for timing as much as priority. Succor needs roughly two seconds of
            // standing still and so needs the START of the window; Soil is instant and lands right up until
            // the hit.
            if (canCastShield &&
                ScholarSettings.Instance.FightLogicSuccorAoe &&
                Spells.Succor.IsKnownAndReady() &&
                Group.CastableParty.Count(x => x.HasPrimaryShield()) < AoeThreshold)
            {
                if (BaseSettings.Instance.DebugFightLogic)
                    Logger.WriteInfo($"[AOE Response] Cast Succor");

                return await FightLogic.DoAndBuffer(ScholarRoutine.SuccorSpell.Cast(Core.Me));
            }

            // Sacred Soil, when no shield fired above.
            if (await TrySacredSoil())
                return true;

            return false;
        }

        private static async Task<bool> JustAoe()
        {
            if (!ScholarSettings.Instance.FightLogicSuccorAoe) return false;

            if (!Spells.Succor.IsKnownAndReady())
                return false;

            // Same caster-plumbing as BigAoe: razor against the detected caster's window, not the
            // player's target — the Shinryu segment waves were answerable for 4-6s and this guard
            // declined every pulse because the idle main body was targeted.
            var enemyTarget = FightLogic.DetectedCaster() ?? Core.Me.CurrentTarget as Character;
            if (enemyTarget == null || !enemyTarget.IsCasting)
                return false;
            if (enemyTarget.SpellCastInfo.RemainingCastTime <= Spells.Succor.AdjustedCastTime)
            {
                Logger.WriteInfo($"Enemy Target: {enemyTarget}\t Remaining Cast Time {enemyTarget.SpellCastInfo.RemainingCastTime}\t Succor Cast Time {Spells.Succor.AdjustedCastTime}\t Logic Challenge: {enemyTarget.SpellCastInfo.RemainingCastTime <= Spells.Succor.AdjustedCastTime}");
                return false;
            }

            if (Core.Me.HasAura(Auras.EmergencyTactics))
                return false;

            return await FightLogic.DoAndBuffer(ScholarRoutine.SuccorSpell.Heal(Core.Me));
        }

        public static async Task<bool> Tankbuster()
        {
            // No party gate: fight-logic mitigation should fire solo too (self-target) — e.g. Occult
            // Crescent field ops. Reactions below target Core.Me or fall back to self.
            if (!FightLogic.ZoneHasFightLogic())
                return false;

            var target = FightLogic.EnemyIsCastingTankBuster();

            if (target == null)
            {
                // Mirror the other healers: also react to shared tankbusters (self-target solo, co-tank
                // in party) for bosses that define both a regular and a shared tankbuster list.
                target = FightLogic.EnemyIsCastingSharedTankBuster();

                if (target == null)
                    return false;
            }

            if (!FightLogic.HodlCastTimeRemaining(hodlTillDurationInPct: BaseSettings.Instance.FightLogicResponseDelay))
                return false;

            // In a party this shields the co-tanks when the buster is on the other tank; solo it's a dead
            // loop over an empty tank list that returns true with nothing cast (buster unmitigated), so
            // gate it to party and let the solo path fall through to the self-shields below.
            if (Globals.InParty && !target.BeingTargetedBy(Core.Me.CurrentTarget))
            {
                // Shield one unshielded tank per pulse instead of spinning the whole tank list in a single
                // frame — the old while+Yield cast Adloquium (a ~2s cast) on each tank back-to-back and
                // froze the routine for seconds. Successive pulses cover the remaining tanks.
                var unshieldedTank = Group.CastableTanks.FirstOrDefault(r => !r.HasAura(Auras.Galvanize));
                if (unshieldedTank != null)
                    return await FightLogic.DoAndBuffer(ScholarRoutine.AdloquiumSpell.Heal(unshieldedTank));

                return true;
            }


            if (ScholarSettings.Instance.FightLogicExcogTank &&
                Spells.Excogitation.IsKnownAndReady() &&
                Core.Me.HasAetherflow() &&
                !target.HasAura(Auras.Excogitation))
                return await FightLogic.DoAndBuffer(Spells.Excogitation.CastAura(target, Auras.Excogitation));


            if (ScholarSettings.Instance.FightLogicAdloTank &&
                Spells.Adloquium.IsKnownAndReady() &&
                !target.HasAura(Auras.Galvanize))
                return await FightLogic.DoAndBuffer(ScholarRoutine.AdloquiumSpell.HealAura(target, Auras.Galvanize));

            return false;
        }

        public static int AoeThreshold => PartyManager.NumMembers > 4 ? ScholarSettings.Instance.AoeNeedHealingFullParty : ScholarSettings.Instance.AoeNeedHealingLightParty;

    }
}
