"""Offline, synthetic capture diagnostics; these tests never attach to a client."""
import io
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest import mock

from analyze_execution_capture import analyze
from native_projectile_rng import ProjectileRandom, object_seed


def record(kind, **fields):
    return dict(projectileExecution=1, type=kind, **fields)


def action(owner=10, stage=0):
    return dict(owner=owner, sequence=12, derived=2, stage=stage)


def result_snapshot(**fields):
    return dict(key=action(), target=20, auxiliary34=0, flags=1, code=0, kind0=0, kind1=1, **fields)


class ResultIngressTests(unittest.TestCase):
    def run_records(self, records, coverage=True):
        import json
        start = record('start', n=0)
        if coverage:
            start['observations'] = dict(resultIngress=1)
        rows = [start] + [dict(row, n=i) for i, row in enumerate(records, 1)]
        rows.append(record('end', n=len(rows), dropped=0, errors=0))
        return analyze(io.StringIO('\n'.join(json.dumps(row) for row in rows)))

    def control(self, call=1, process=1):
        return record('result_control_begin', call=call, tid=7, actor='0x100', key=action(),
                      target=20, processByte=process, caller='0x1421952bb')

    def begin(self, call=2, caller='0x141e90e04', control=1, missing=False):
        return record('result_process_begin', call=call, tid=7, actor='0x100', before=result_snapshot(),
                      caller=caller, controlCall=control, inputAction='0x0' if missing else '0x300',
                      inputActionMissing=missing, inputActionKey=None if missing else action(),
                      statsHandle=['0x200', '0x208'])

    def end(self, call=2, completed=True):
        after = result_snapshot()
        after['flags'] = 3
        return record('result_process_end', call=call, tid=7, after=after, completed=completed)

    def match(self, call=1):
        return record('result_control_match', call=call, tid=7, record=result_snapshot())

    def control_end(self, call=1, matched=True):
        return record('result_control_end', call=call, tid=7, matched=matched)

    def test_remove_only_and_missing_are_normal_without_processing(self):
        result = self.run_records([self.control(process=0), self.match(), self.control_end(),
                                  self.control(call=2, process=255), self.control_end(call=2, matched=False)])
        self.assertTrue(result['complete'], result['diagnostics'])
        self.assertEqual(result['results']['control_remove_only'], 1)
        self.assertEqual(result['results']['control_missing'], 1)
        self.assertEqual(result['result_observation']['covered_sessions'], 1)

    def test_process_sources_flags_and_found_are_independent(self):
        events = [self.control(), self.match(), self.begin(), self.end(completed=False), self.control_end(),
                  record('consume', key=action(), target=20, found=True)]
        for call, caller in [(3, '0x141e9109e'), (4, '0x14193a106'), (5, '0x141e914b5'), (6, '0x1234')]:
            events += [self.begin(call, caller, None, missing=True), self.end(call, completed=call != 4)]
        result = self.run_records(events)
        self.assertTrue(result['complete'], result['diagnostics'])
        counts = result['results']
        for label in ['1b0_retained_found', '1af_immediate', 'exact_consume', 'batch', 'unknown']:
            self.assertEqual(counts['process_source_' + label], 1)
        self.assertEqual(counts['process_incomplete'], 2)
        self.assertEqual(counts['process_input_action_missing'], 4)
        self.assertEqual(counts['process_flags_changed'], 5)
        self.assertEqual(counts['success_without_prior_candidate'], 1)

    def test_prior_arrivals_are_candidates_even_after_remove_only(self):
        result = self.run_records([record('result', **result_snapshot(), caller='0x1421952a5'),
                                  self.control(process=0), self.match(), self.control_end(),
                                  record('consume', key=action(), target=20, found=True)])
        self.assertTrue(result['complete'], result['diagnostics'])
        self.assertEqual(result['results']['control_match_with_prior_candidate'], 1)
        self.assertEqual(result['results']['success_with_prior_candidate'], 1)

    def test_result_and_rng_stacks_are_independent_and_nested(self):
        result = self.run_records([record('scope_enter', call=10, tid=7), self.control(), self.match(),
                                  self.begin(), self.begin(3, '0x141e914b5', None), self.end(3), self.end(),
                                  self.control_end(), record('scope_exit', call=10, tid=7)])
        self.assertTrue(result['complete'], result['diagnostics'])

    def test_invalid_match_width_identity_duplicate_and_nesting(self):
        bad_width = self.match()
        bad_width['record']['key']['derived'] = 256
        wrong_key = self.match()
        wrong_key['record']['target'] = 21
        for events in [[self.control(), bad_width], [self.control(), wrong_key],
                       [self.control(), self.match(), self.match()],
                       [self.control(), self.match(), self.begin(), self.control_end()],
                       [self.control(), self.control_end(matched=False), self.control()],
                       [self.control(process=0), self.match(), self.begin()]]:
            result = self.run_records(events)
            self.assertFalse(result['complete'], events)

    def test_process_parent_and_action_pointer_are_checked(self):
        for changes in [dict(controlCall=99), dict(inputActionMissing=True), dict(statsHandle=['0x200']),
                        dict(tid=8)]:
            begin = self.begin()
            begin.update(changes)
            self.assertFalse(self.run_records([self.control(), self.match(), begin, self.end(),
                                              self.control_end()])['complete'])

    def test_legacy_profile_does_not_claim_zero_observed_controls(self):
        result = self.run_records([], coverage=False)
        self.assertTrue(result['complete'])
        self.assertEqual(result['result_observation']['uncovered_sessions'], 1)
        self.assertEqual(result['result_observation']['covered_sessions'], 0)
        self.assertFalse(self.run_records([self.begin(2, '0x141e9109e', None), self.end()],
                                          coverage=False)['complete'])

    def test_unreturned_control_or_process_is_incomplete(self):
        for events in [[self.control()], [self.begin(2, '0x141e9109e', None)]]:
            self.assertFalse(self.run_records(events)['complete'])

    def test_full_result_width_and_completion_type_are_validated(self):
        for field, value in [('flags', 65536), ('auxiliary34', -1), ('code', 256), ('kind1', True)]:
            begin = self.begin(2, '0x141e9109e', None)
            begin['before'][field] = value
            self.assertFalse(self.run_records([begin, self.end()])['complete'])
        end = self.end()
        end['completed'] = 1
        self.assertFalse(self.run_records([self.begin(2, '0x141e9109e', None), end])['complete'])

    def test_missing_input_action_can_complete_but_must_match_known_caller(self):
        result = self.run_records([self.control(), self.match(),
                                  self.begin(caller='0x141e90e22', missing=True), self.end(), self.control_end()])
        self.assertTrue(result['complete'], result['diagnostics'])
        self.assertEqual(result['results']['process_source_1b0_retained_missing'], 1)
        self.assertFalse(self.run_records([self.control(), self.match(), self.begin(missing=True), self.end(),
                                          self.control_end()])['complete'])

    def test_bad_record_type_or_coverage_is_diagnostic(self):
        import json
        for row in [record(1, n=0), record('start', n=0, observations=[]),
                    record('start', n=0, observations=dict(resultIngress=True)),
                    record('start', n=0, observations=dict(resultIngress=2))]:
            self.assertFalse(analyze(io.StringIO(json.dumps(row)))['complete'])

    def test_new_profile_arrival_fields_are_checked_without_breaking_legacy(self):
        for field, value in [('flags', -1), ('code', 9999), ('kind1', 9999), ('caller', 'invalid')]:
            arrival = record('result', **result_snapshot(), caller='0x1421952a5')
            arrival[field] = value
            self.assertFalse(self.run_records([arrival])['complete'])
        self.assertTrue(self.run_records([record('result', key=action(), target=20, kind0=0)],
                                         coverage=False)['complete'])


class CaptureTests(unittest.TestCase):
    def run_records(self, records):
        import json
        rows = [record('start', n=0, profile='3.29.3b', compatible=True)]
        rows += [dict(row, n=i) for i, row in enumerate(records, 1)]
        rows += [record('end', n=len(rows), dropped=0, errors=0)]
        return analyze(io.StringIO('\n'.join(json.dumps(row) for row in rows)))

    def test_rng_reuse_valid_and_mismatch(self):
        rng = ProjectileRandom([1, 2, 3, 4, 5])
        before = [rng.steps, *rng.state]
        sample = rng.bounded(3001)
        after = [rng.steps, *rng.state]
        result = self.run_records([
            record('rng_init', words=[1, 2, 3, 4, 5], after=before, state='0x10'),
            record('rng_bound', before=before, after=after, bound=3001, sample=sample,
                   wordBits=32, mask=4294967295, state='0x10'),
            record('rng_bound', before=before, after=after, bound=3001, sample=sample ^ 1,
                   wordBits=32, mask=4294967295, state='0x10'),
        ])
        self.assertEqual(result['rng']['checked'], 3)
        self.assertEqual(result['rng']['mismatches'], 1)

    def test_duplicate_keys_owners_and_immediate_results(self):
        result = self.run_records([
            record('result', key=action(), target=20, kind0=0),
            record('result', key=action(), target=20, kind0=0),
            record('result', key=action(owner=11), target=20, kind0=1),
            record('consume', key=action(), target=20, found=True),
            record('consume', key=action(owner=11), target=20, found=True),
            record('consume', key=action(stage=1), target=20, found=False),
        ])
        self.assertEqual(result['results']['success_with_prior_candidate'], 1)
        self.assertEqual(result['results']['success_without_prior_candidate'], 1)
        self.assertEqual(result['results']['missing'], 1)
        self.assertEqual(result['results']['duplicate_arrivals'], 1)

    def test_nested_calls_and_multiple_sessions(self):
        import json
        events = [record('start', n=0), record('scope_enter', n=1, call=1, tid=1),
                  record('scope_enter', n=2, call=2, tid=1),
                  record('scope_exit', n=3, call=2, tid=1),
                  record('scope_exit', n=4, call=1, tid=1), record('end', n=5),
                  record('start', n=0), record('end', n=1)]
        result = analyze(io.StringIO('\n'.join(json.dumps(e) for e in events)))
        self.assertEqual(result['sessions'], 2)
        self.assertEqual(result['diagnostics'], [])

    def test_truncated_cap_error_and_reordered_records(self):
        import json
        events = [record('start', n=0), record('scope_enter', n=2, call=1, tid=1),
                  record('cap', n=1, dropped=1), record('error', n=3, error='unreadable')]
        result = analyze(io.StringIO('\n'.join(json.dumps(e) for e in events) + '\n{"type":'))
        self.assertFalse(result['complete'])
        self.assertGreaterEqual(len(result['diagnostics']), 5)

    def test_frida_envelopes_and_unrelated_runner_lines(self):
        import json
        events = [{'loaded': 'probe.js'}, {'type': 'send', 'payload': record('start', n=0)},
                  {'type': 'send', 'payload': record('end', n=1)}, {'detached': 1}]
        result = analyze(io.StringIO('\n'.join(json.dumps(e) for e in events)))
        self.assertTrue(result['complete'])
        self.assertEqual(result['events'], 2)

    def test_empty_or_malformed_is_not_a_successful_capture(self):
        self.assertFalse(analyze(io.StringIO(''))['complete'])
        self.assertFalse(analyze(io.StringIO('{"projectileExecution":1,"type":"rng_bound"}\n'))['complete'])

    def test_object_hash_and_unsupported_wrapper_are_separate(self):
        key = action()
        result = self.run_records([
            record('object_seed', key=key, newDerived=3, ordinal=0,
                   seed=object_seed(12, 10, 2, 3, 0)),
            record('rng_bound', before=[8, 0, 0, 0, 0], after=[9, 0, 0, 0, 0],
                   wordBits=16, mask=65535, bound=3, sample=2, state='0x10')])
        self.assertEqual(result['rng']['checked'], 1)
        self.assertEqual(result['rng']['unsupported_wrapper'], 1)

    def test_stat_update_signed_values_full_skill_id_and_pointer_candidates(self):
        # A later view may use a wrapper instead of the raw updated object.
        result = self.run_records([
            record('skill_stat_update', update=1, owner=10, skillId=0x8002, selector=0,
                   pairCount=1, pairsTruncated=False, pairs=[dict(statKey=25, value=-20)]),
            record('skill_stat_apply', update=1, stats='0x100', statKey=25, value=-20),
            record('skill_stat_end', update=1),
            record('stat_view', call=2, key=action(), selector=0, skillId=0x8002,
                   selected='0x200', rawPrimary='0x100', effectivePrimary='0x200'),
            record('stat_view', call=3, key=action(), selector=0, skillId=0x8002,
                   selected='0x100', rawPrimary='0x100', effectivePrimary='0x100'),
        ])
        self.assertTrue(result['complete'])
        self.assertEqual(result.get('stats', {}).get('updates'), 1)
        self.assertEqual(result['stats']['applied_pairs'], 1)
        self.assertEqual(result['stats']['views'], 2)
        self.assertEqual(result['stats']['views_with_prior_pointer_candidate'], 1)

    def test_stat_truncation_and_invalid_signed_pair_are_diagnostics(self):
        for pair in [dict(statKey=1, value=0x80000000), dict(statKey=True, value=1)]:
            result = self.run_records([
                record('skill_stat_update', update=1, owner=10, skillId=0x8002, selector=0,
                       pairCount=1, pairsTruncated=False, pairs=[pair])])
            self.assertFalse(result['complete'])
        result = self.run_records([
            record('skill_stat_update', update=1, owner=10, skillId=0x8002, selector=0,
                   pairCount=300, pairsTruncated=True, pairs=[])])
        self.assertFalse(result['complete'])

    def test_stat_pointer_candidates_do_not_cross_capture_sessions(self):
        import json
        events = [record('start', n=0),
                  record('skill_stat_update', n=1, update=1, owner=10, skillId=1, selector=0,
                         pairCount=1, pairsTruncated=False, pairs=[dict(statKey=1, value=2)]),
                  record('skill_stat_apply', n=2, update=1, stats='0x100', statKey=1, value=2),
                  record('skill_stat_end', n=3, update=1),
                  record('end', n=4), record('start', n=0),
                  record('stat_view', n=1, call=1, key=action(), selector=0, skillId=1,
                         selected='0x100', rawPrimary='0x100', effectivePrimary='0x100'),
                  record('end', n=2)]
        result = analyze(io.StringIO('\n'.join(json.dumps(e) for e in events)))
        self.assertTrue(result['complete'])
        self.assertIn('stats', result)
        self.assertEqual(result['stats'].get('views_with_prior_pointer_candidate', 0), 0)

    def test_stat_setter_requires_the_enclosing_observed_pair(self):
        for pair in [dict(statKey=2, value=3), dict(statKey=1, value=4)]:
            result = self.run_records([
                record('skill_stat_update', update=1, owner=10, skillId=1, selector=0,
                       pairCount=1, pairsTruncated=False, pairs=[dict(statKey=1, value=3)]),
                record('skill_stat_apply', update=1, stats='0x100', **pair)])
            self.assertFalse(result['complete'])

    def test_unreturned_stat_dispatch_is_incomplete_even_with_capture_end(self):
        result = self.run_records([
            record('skill_stat_update', update=1, owner=10, skillId=1, selector=0,
                   pairCount=0, pairsTruncated=False, pairs=[])])
        self.assertFalse(result['complete'])

    def test_animation_request_seed_wrap_and_zero_fallback_candidate(self):
        key = action()
        key['sequence'] = 65535
        result = self.run_records([
            record('scope_enter', call=9, tid=1, name='root_start'),
            record('animation_request', call=9, tid=1, key=key, request='0x1200',
                   requestSeed=65534, outerRemaining=4294967295, actorSeed=7),
            record('animation_request', call=9, tid=1, key=action(), request='0x1300',
                   requestSeed=0, outerRemaining=4294967284, actorSeed=8),
            record('scope_exit', call=9, tid=1, name='root_start')])
        self.assertTrue(result['complete'], result['diagnostics'])
        self.assertIn('launch', result)
        self.assertEqual(result['launch']['checked_requests'], 2)
        self.assertEqual(result['launch']['zero_seed_fallback_candidates'], 1)

    def test_animation_request_mismatch_or_missing_scope_is_incomplete(self):
        for seed, call in [(99, 9), (13, 10)]:
            result = self.run_records([
                record('scope_enter', call=9, tid=1, name='root_start'),
                record('animation_request', call=call, tid=1, key=action(), request='0x1200',
                       requestSeed=seed, outerRemaining=1, actorSeed=7),
                record('scope_exit', call=9, tid=1, name='root_start')])
            self.assertFalse(result['complete'])

    def test_animation_event_is_candidate_and_does_not_prove_release(self):
        result = self.run_records([
            record('animation_event', key=action(), action='0x1000', eventHash=0x9b8d,
                   eventType=0, descriptorKind=0, actorFlags=2, channel='normal')])
        self.assertTrue(result['complete'], result['diagnostics'])
        self.assertIn('launch', result)
        self.assertEqual(result['launch']['event_candidates'], 1)
        self.assertNotIn('confirmed_releases', result['launch'])


class TlsRandomTests(unittest.TestCase):
    def run_records(self, records, coverage=True, version=1):
        import json
        rows = [record('start', n=0, observations={'tlsRandom': version} if coverage else {})]
        rows += [dict(row, n=i) for i, row in enumerate(records, 1)]
        rows.append(record('end', n=len(rows), dropped=0, errors=0))
        return analyze(io.StringIO('\n'.join(json.dumps(row) for row in rows)))

    def bits(self, call, before, sample, after, chance=10):
        return [record('tls_bits_begin', call=call, tid=7, scope=1, bits=7,
                       caller='0x140e3b48b', chanceCall=chance),
                record('tls_step', call=call, tid=7, index=0, bits=7, before=before, state='0x8000'),
                record('tls_bits_end', call=call, tid=7, sample=sample, after=after)]

    def test_rejected_tls_sample_and_chance_match_the_actual_steps(self):
        rows = [record('scope_enter', call=1, tid=7),
                record('tls_chance_begin', call=10, tid=7, scope=1, p=50, caller='0x141ba970d')]
        rows += self.bits(11, 1103527590, 126, 2524885223)
        rows += self.bits(12, 2524885223, 1, 662824084)
        rows += [record('tls_chance_end', call=10, tid=7, success=True),
                 record('scope_exit', call=1, tid=7)]
        result = self.run_records(rows)
        self.assertTrue(result['complete'], result['diagnostics'])
        self.assertEqual(result['tls']['checked_bits'], 2)
        self.assertEqual(result['tls']['rejected_samples'], 1)
        self.assertEqual(result['tls']['checked_chances'], 1)
        # Corrupt an observed state, the returned sample, or the final chance result.
        for index, field, value in [(3, 'before', 1), (4, 'sample', 125), (8, 'success', False)]:
            corrupted = [dict(row) for row in rows]
            corrupted[index][field] = value
            self.assertFalse(self.run_records(corrupted)['complete'], (index, field))

    def test_tls_no_draw_boundaries_and_missing_coverage(self):
        for p, success in [(0, False), (100, True), (4294967295, True)]:
            rows = [record('scope_enter', call=1, tid=7),
                    record('tls_chance_begin', call=10, tid=7, scope=1, p=p, caller='0x141ba970d'),
                    record('tls_chance_end', call=10, tid=7, success=success),
                    record('scope_exit', call=1, tid=7)]
            self.assertTrue(self.run_records(rows)['complete'])
            self.assertFalse(self.run_records(rows, coverage=False)['complete'])

    def test_tls_wide_chunks_and_truncation_are_checked(self):
        # 32 bits concatenate 15+15+2; each chunk starts with its own observed TLS state.
        states = [1, 1103527590, 2524885223, 662824084]
        sample = 0
        chunks = []
        for i, bits in enumerate([15, 15, 2]):
            sample = ((sample << bits) + ((states[i+1] >> 16) & ((1 << bits)-1))) & 0xffffffff
            chunks.append(record('tls_step', call=2, tid=7, index=i, bits=bits,
                                 before=states[i], state='0x8000'))
        rows = [record('scope_enter', call=1, tid=7),
                record('tls_bits_begin', call=2, tid=7, scope=1, bits=32,
                       caller='0x141baa22b', chanceCall=None), *chunks,
                record('tls_bits_end', call=2, tid=7, sample=sample, after=states[-1]),
                record('scope_exit', call=1, tid=7)]
        self.assertTrue(self.run_records(rows)['complete'])
        self.assertFalse(self.run_records(rows[:-2])['complete'])

    def test_tls_zero_bits_version_and_ended_scope(self):
        rows = [record('scope_enter', call=1, tid=7),
                record('tls_bits_begin', call=2, tid=7, scope=1, bits=0,
                       caller='0x141baa22b', chanceCall=None),
                record('tls_bits_end', call=2, tid=7, sample=0, after=None),
                record('scope_exit', call=1, tid=7)]
        self.assertTrue(self.run_records(rows)['complete'])
        for version in [True, 2, '1']:
            self.assertFalse(self.run_records([], version=version)['complete'])
        self.assertFalse(self.run_records([rows[0], rows[1], rows[3], rows[2]])['complete'])


class WindRandomTests(unittest.TestCase):
    """The inline TS wind draw is distinct from helper-based TLS observations."""

    def run_records(self, records, version=1):
        import json
        observations = {} if version is None else {'windRandom': version}
        rows = [record('start', n=0, observations=observations)]
        rows += [dict(row, n=i) for i, row in enumerate(records, 1)]
        rows.append(record('end', n=len(rows), dropped=0, errors=0))
        return analyze(io.StringIO('\n'.join(json.dumps(row) for row in rows)))

    def wind(self, call=1, tid=7, before=1, caller='0x1414fc32e'):
        after = (before * 0x41c64e6d + 0x3039) & 0xffffffff
        return [record('scope_enter', call=call, tid=tid, name='tornadoWind'),
                record('wind_begin', call=call, tid=tid, action='0x1000', vtable='0x143423898',
                       key=action(), caller=caller, callerRaw=caller, frames=[caller, None],
                       grid=[-5, 6], heightMarker=-9),
                record('wind_before', call=call, tid=tid, action='0x1000', state='0x8170', before=before),
                record('wind_after', call=call, tid=tid, action='0x1000', state='0x8170',
                       cpuAfter=after, after=after),
                record('wind_end', call=call, tid=tid, action='0x1000', draws=1),
                record('scope_exit', call=call, tid=tid)]

    def test_inline_draws_are_checked_without_helper_events_or_known_outer_tls_history(self):
        rows = self.wind(before=1) + self.wind(call=2, before=0xffffffff, caller='0x7ff700001234')
        rows[7]['caller'] = None
        rows[7]['frames'] = [None]
        result = self.run_records(rows)
        self.assertTrue(result['complete'], result['diagnostics'])
        self.assertEqual(result['wind']['checked_draws'], 2)
        self.assertEqual(result['wind']['checked_calls'], 2)
        self.assertEqual(result['wind']['route_scheduled'], 1)
        self.assertEqual(result['wind']['route_unknown'], 1)
        self.assertEqual(result['wind_observation']['covered_sessions'], 1)
        self.assertNotIn('continuous_tls_history', result['wind'])

    def test_terminal_and_unknown_routes_and_appended_sessions_remain_separate(self):
        import json
        rows = self.wind(caller='0x1414fbdec') + self.wind(call=2, caller='0x140001234')
        result = self.run_records(rows)
        self.assertTrue(result['complete'], result['diagnostics'])
        self.assertEqual(result['wind']['route_terminal'], 1)
        self.assertEqual(result['wind']['route_unknown'], 1)
        records = [record('start', n=0, observations={'windRandom': 1})]
        records += [dict(row, n=i) for i, row in enumerate(self.wind(), 1)]
        records.append(record('end', n=7, dropped=0, errors=0))
        records += [record('start', n=0), record('end', n=1, dropped=0, errors=0)]
        result = analyze(io.StringIO('\n'.join(json.dumps(row) for row in records)))
        self.assertTrue(result['complete'], result['diagnostics'])
        self.assertEqual(result['wind_observation'], {'covered_sessions': 1, 'uncovered_sessions': 1})

    def test_cpu_arithmetic_and_actual_memory_after_store_are_separate_checks(self):
        for index, field, value in [(2, 'before', 2), (3, 'cpuAfter', 0), (3, 'after', 0),
                                    (3, 'state', '0x9170'), (3, 'action', '0x2000'),
                                    (4, 'draws', 0), (1, 'vtable', '0x14333ac40'),
                                    (1, 'frames', ['0x700000000000'])]:
            rows = self.wind()
            rows[index][field] = value
            self.assertFalse(self.run_records(rows)['complete'], (index, field))

    def test_missing_duplicate_late_and_unscoped_records_are_incomplete(self):
        rows = self.wind()
        for changed in [rows[:2] + rows[3:], rows[:3] + [rows[2]] + rows[3:],
                        rows[:4] + rows[5:], rows[:3] + [rows[5], rows[3], rows[4]], rows[1:],
                        rows[:4] + [rows[3]] + rows[4:], rows[:2] + rows[4:], rows[:-2]]:
            self.assertFalse(self.run_records(changed)['complete'], changed)

    def test_independent_threads_and_nested_winds_pair_by_action_and_invocation(self):
        a, b, c = self.wind(), self.wind(call=2, tid=8), self.wind(call=3)
        rows = a[:2] + b[:2] + c + b[2:] + a[2:]
        result = self.run_records(rows)
        self.assertTrue(result['complete'], result['diagnostics'])
        self.assertEqual(result['wind']['checked_calls'], 3)
        swapped = self.wind()
        swapped[3]['tid'] = 8
        self.assertFalse(self.run_records(swapped)['complete'])

    def test_coverage_versions_legacy_sessions_and_input_widths(self):
        for version in [None, True, '1', 2]:
            self.assertFalse(self.run_records(self.wind(), version=version)['complete'], version)
        legacy = self.run_records([], version=None)
        self.assertTrue(legacy['complete'], legacy['diagnostics'])
        self.assertEqual(legacy['wind_observation']['uncovered_sessions'], 1)
        for field, value in [('heightMarker', -(1 << 31)-1), ('grid', [0, 1 << 31]),
                             ('frames', [None]*13), ('key', {**action(), 'derived': 256})]:
            rows = self.wind()
            rows[1][field] = value
            self.assertFalse(self.run_records(rows)['complete'], field)


class ContextReceiverTests(unittest.TestCase):
    """Actual store values, borrowed-input changes, and independent callback pairing."""

    def run_records(self, records, version=1):
        import json
        observations = {} if version is None else {'contextReceiver': version}
        rows = [record('start', n=0, observations=observations)]
        rows += [dict(row, n=i) for i, row in enumerate(records, 1)]
        rows.append(record('end', n=len(rows), dropped=0, errors=0))
        return analyze(io.StringIO('\n'.join(json.dumps(row) for row in rows)))

    def begin(self, call=1, tid=7, parent=None, value=-25):
        return record('context_begin', call=call, tid=tid, parentCall=parent, action='0x1000',
                      vtable='0x14333ac40', key=action(), context='0x2000', input1c=value,
                      before434=33, callerRaw='0x7ff700001234', caller='0x140001234')

    def store(self, call=1, tid=7, value=-25):
        return record('context_assign', call=call, tid=tid, action='0x1000', assigned434=value)

    def end(self, call=1, tid=7, value=-25):
        return record('context_end', call=call, tid=tid, action='0x1000', after434=value)

    def test_signed_actual_store_and_changed_borrowed_input_need_no_projectile_scope(self):
        rows = [self.begin(), self.store(), self.end(),
                self.begin(2, value=999), self.store(2, value=0), self.end(2, value=0)]
        rows[3]['caller'] = None  # A caller outside the main image has only a raw address.
        result = self.run_records(rows)
        self.assertTrue(result['complete'], result['diagnostics'])
        self.assertEqual(result['context']['checked_assignments'], 2)
        self.assertEqual(result['context']['entry_input_changed'], 1)
        self.assertEqual(result['context']['zero_assignments'], 1)
        self.assertEqual(result['context_observation']['covered_sessions'], 1)

    def test_nested_same_action_and_interleaved_threads_are_independent(self):
        rows = [self.begin(), self.begin(2, parent=1, value=9),
                self.begin(3, tid=8, value=7), self.store(3, tid=8, value=7), self.end(3, tid=8, value=7),
                self.store(2, value=9), self.end(2, value=9), self.store(), self.end()]
        result = self.run_records(rows)
        self.assertTrue(result['complete'], result['diagnostics'])
        self.assertEqual(result['context']['checked_assignments'], 3)

    def test_legacy_and_unknown_coverage_cannot_claim_context_observation(self):
        legacy = self.run_records([], version=None)
        self.assertTrue(legacy['complete'])
        self.assertEqual(legacy['context_observation']['uncovered_sessions'], 1)
        self.assertFalse(self.run_records([self.begin(), self.store(), self.end()], version=None)['complete'])
        for version in [True, 2, '1']:
            self.assertFalse(self.run_records([], version=version)['complete'])

    def test_missing_duplicate_late_or_mispaired_store_is_incomplete(self):
        for rows in [[self.begin()], [self.begin(), self.end()], [self.store()],
                     [self.begin(), self.store(), self.store(), self.end()],
                     [self.begin(), self.store(), self.end(), self.store()],
                     [self.begin(), self.store(tid=8), self.end()],
                     [self.begin(), self.begin(2, parent=1), self.store(), self.end()],
                     [self.begin(), self.store(), self.end(), self.begin()]]:
            self.assertFalse(self.run_records(rows)['complete'], rows)

    def test_width_pointer_parent_and_final_class_validation(self):
        for field,value in [('input1c', True), ('input1c', 2147483648), ('before434', -2147483649),
                            ('action', '0x0'), ('context', '0x0'), ('vtable', '0x143335d80'),
                            ('callerRaw', 'invalid'), ('caller', '0x7ff700001234'), ('parentCall', 99)]:
            begin = self.begin()
            begin[field] = value
            self.assertFalse(self.run_records([begin, self.store(), self.end()])['complete'], field)
        for field,value in [('assigned434', -2147483649), ('action', '0x1100')]:
            store = self.store()
            store[field] = value
            self.assertFalse(self.run_records([self.begin(), store, self.end()])['complete'], field)
        end = self.end(value=0)
        result = self.run_records([self.begin(), self.store(), end])
        self.assertFalse(result['complete'])
        self.assertEqual(result['context']['assignment_mismatches'], 1)

    def test_appended_sessions_reset_ids_and_do_not_hide_unfinished_calls(self):
        import json
        def session(rows, covered=True):
            records = [record('start', n=0, observations={'contextReceiver': 1} if covered else {})]
            records += [dict(row, n=i) for i,row in enumerate(rows,1)]
            records.append(record('end', n=len(records), dropped=0, errors=0))
            return '\n'.join(json.dumps(row) for row in records) + '\n'
        full = session([self.begin(), self.store(), self.end()])
        result = analyze(io.StringIO(full + full + session([], covered=False)))
        self.assertTrue(result['complete'], result['diagnostics'])
        self.assertEqual(result['context']['checked_assignments'], 2)
        self.assertEqual(result['context_observation'], {'covered_sessions': 2, 'uncovered_sessions': 1})
        self.assertFalse(analyze(io.StringIO(session([self.begin()]) + full))['complete'])


class RunnerFinalizerTests(unittest.TestCase):
    """Exercise runner lifecycle with a fake device/session; no Frida attach."""

    def run_fake(self, exported=True, fail=False, load_fail=False):
        path = Path(__file__).parents[1] / 'frida' / 'run.py'
        spec = importlib.util.spec_from_file_location('projectile_capture_runner', path)
        runner = importlib.util.module_from_spec(spec)
        # The runner lifecycle tests do not require Frida or its native library.
        with mock.patch.dict('sys.modules', {'frida': mock.Mock()}):
            spec.loader.exec_module(runner)
        calls = []
        script, session, device = mock.Mock(), mock.Mock(), mock.Mock()
        script.list_exports_sync.return_value = ['finish'] if exported else []

        def finish():
            calls.append('finish')
            if fail:
                raise RuntimeError('synthetic finalizer failure')

        script.exports_sync.finish.side_effect = finish
        if load_fail:
            script.load.side_effect = ValueError('synthetic incompatible profile')
        session.create_script.return_value = script
        session.detach.side_effect = lambda: calls.append('detach')
        device.attach.return_value = session
        with tempfile.TemporaryDirectory() as folder:
            probe = Path(folder) / 'probe.js'
            probe.write_text('// synthetic; not executed', encoding='utf-8')
            with mock.patch.object(runner.frida, 'get_local_device', return_value=device), \
                 mock.patch.object(runner.threading, 'Event') as event, \
                 mock.patch('sys.argv', ['run.py', '123', '--finish-rpc', 'finish', str(probe)]), \
                 mock.patch('sys.stdout', new_callable=io.StringIO):
                # Stop the listen loop without pretending the separate session-loss event fired.
                event.side_effect = [mock.Mock(is_set=lambda: True), mock.Mock(is_set=lambda: False)]
                if load_fail:
                    with self.assertRaisesRegex(ValueError, 'incompatible profile'):
                        runner.main()
                else:
                    runner.main()
        return calls

    def test_finish_before_detach(self):
        self.assertEqual(self.run_fake(), ['finish', 'detach'])

    def test_finalizer_exception_still_detaches(self):
        self.assertEqual(self.run_fake(fail=True), ['finish', 'detach'])

    def test_legacy_probe_without_finalizer_still_detaches(self):
        self.assertEqual(self.run_fake(exported=False), ['detach'])

    def test_rejected_probe_detaches_session(self):
        self.assertEqual(self.run_fake(load_fail=True), ['detach'])


if __name__ == '__main__':
    unittest.main()
