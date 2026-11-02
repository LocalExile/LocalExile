#!/usr/bin/env python3
"""Offline diagnostics for trace-projectile-execution.js JSONL; never attaches.

RNG comparisons use static arithmetic references, not native execution.
Result-arrival correlation is deliberately not a simulation of the pending queue.
Exit 0 means a complete recording without diagnostic failures, not game parity.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import json
from typing import Iterable

from native_projectile_rng import ProjectileRandom, object_seed
from native_tornado_rng import TerminalRandom


def uint(value, bits=32):
    if type(value) is not int or not 0 <= value < (1 << bits):
        raise ValueError(f'expected uint{bits}')
    return value


def state(value):
    if not isinstance(value, list) or len(value) != 5:
        raise ValueError('state must contain counter and four words')
    return [uint(word) for word in value]


def stat_pair(row):
    key, value = uint(row['statKey']), row['value']
    if type(value) is not int or not -(1 << 31) <= value < (1 << 31):
        raise ValueError('expected int32 stat value')
    return key, value


def address(value):
    if not isinstance(value, str) or not value.startswith('0x'):
        raise ValueError('expected hexadecimal pointer')
    return uint(int(value, 16), 64)


def int32(value):
    if type(value) is not int or not -(1 << 31) <= value < (1 << 31):
        raise ValueError('expected int32 context value')
    return value


def result_key(row):
    key = row['key']
    return (uint(key['owner']), uint(key['sequence'], 16), uint(key['derived'], 8),
            uint(key['stage'], 8), uint(row['target']))


PROCESS_SOURCES = {
    0x141e9109e: '1af_immediate',
    0x141e90e04: '1b0_retained_found', 0x141e90e22: '1b0_retained_missing',
    0x141e90ec6: '1b0_root_found', 0x141e90ee8: '1b0_root_missing',
    0x14193a106: 'exact_consume', 0x141e914b5: 'batch',
}

CONTEXT_TABLES = {0x14326d930, 0x1432ec7e8, 0x14333ac40, 0x14326b9c0, 0x143423898}


def result_snapshot(row):
    identity = result_key(row)
    for field, bits in [('auxiliary34', 16), ('flags', 16), ('code', 8), ('kind0', 8), ('kind1', 8)]:
        uint(row[field], bits)
    return identity


def analyze(lines: Iterable[str]) -> dict:
    counts = Counter()
    rng_counts = Counter()
    result_counts = Counter()
    stat_counts = Counter()
    launch_counts = Counter()
    tls_counts = Counter()
    context_counts = Counter()
    context_callers = Counter()
    wind_counts = Counter()
    diagnostics = []
    sessions = events = failures = 0
    active = ended = False
    last_n = -1
    stacks = defaultdict(list)
    arrivals = Counter()
    observed_states = {}
    updates = {}
    active_updates = set()
    updated_pointers = set()
    result_stacks = defaultdict(list)
    result_invocations = {}
    ingress_covered = False
    covered_sessions = 0
    tls_covered = False
    tls_stacks = defaultdict(list)
    tls_invocations = {}
    context_covered = False
    context_covered_sessions = 0
    context_stacks = defaultdict(list)
    context_invocations = set()
    wind_covered = False
    wind_covered_sessions = 0
    wind_stacks = defaultdict(list)
    wind_invocations = set()

    def issue(message):
        nonlocal failures
        failures += 1
        if len(diagnostics) < 100:
            diagnostics.append(message)

    def close_session():
        if active and not ended:
            issue(f'session {sessions}: missing end marker')
        if any(stacks.values()):
            issue(f'session {sessions}: unterminated scope calls')
        if active_updates:
            issue(f'session {sessions}: unterminated skill-stat dispatch')
        if any(result_stacks.values()):
            issue(f'session {sessions}: unterminated result invocation')
        if any(tls_stacks.values()):
            issue(f'session {sessions}: unterminated TLS invocation')
        if any(context_stacks.values()):
            issue(f'session {sessions}: unterminated context invocation')
        if any(wind_stacks.values()):
            issue(f'session {sessions}: unterminated wind invocation')

    for lineno, line in enumerate(lines, 1):
        if not line.strip():
            continue
        try:
            row = json.loads(line)
            if not isinstance(row, dict):
                raise ValueError('record must be an object')
            if row.get('type') == 'send':
                row = row.get('payload')
            if not isinstance(row, dict):
                continue
            if row.get('projectileExecution') != 1:
                if row.get('type') == 'error':
                    issue(f'line {lineno}: Frida script error')
                continue
            events += 1
            kind = row['type']
            if not isinstance(kind, str):
                raise ValueError('record type must be a string')
            counts[kind] += 1
            if kind == 'start':
                close_session()
                sessions += 1
                active, ended, last_n = True, False, -1
                stacks.clear()
                arrivals.clear()
                observed_states.clear()
                updates.clear()
                active_updates.clear()
                updated_pointers.clear()
                result_stacks.clear()
                result_invocations.clear()
                tls_stacks.clear()
                tls_invocations.clear()
                context_stacks.clear()
                context_invocations.clear()
                wind_stacks.clear()
                wind_invocations.clear()
                observations = row.get('observations', {})
                if not isinstance(observations, dict):
                    raise ValueError('observations must be an object')
                wind_version = observations.get('windRandom', 0)
                if type(wind_version) is not int or wind_version not in (0, 1):
                    raise ValueError('unsupported wind observation version')
                wind_covered = wind_version == 1
                wind_covered_sessions += int(wind_covered)
                context_version = observations.get('contextReceiver', 0)
                if type(context_version) is not int or context_version not in (0, 1):
                    raise ValueError('unsupported context receiver observation version')
                context_covered = context_version == 1
                context_covered_sessions += int(context_covered)
                tls_version = observations.get('tlsRandom', 0)
                if type(tls_version) is not int or tls_version not in (0, 1):
                    raise ValueError('unsupported TLS observation version')
                tls_covered = tls_version == 1
                version = observations.get('resultIngress', 0)
                if type(version) is not int or version not in (0, 1):
                    raise ValueError('unsupported result ingress observation version')
                ingress_covered = version == 1
                covered_sessions += int(ingress_covered)
                if row.get('compatible') is False:
                    issue(f'line {lineno}: incompatible hook profile')
            if not active:
                raise ValueError('event before capture start')
            if ended:
                raise ValueError('event after capture end')
            number = uint(row['n'])
            if number != last_n + 1:
                issue(f'line {lineno}: event number gap/reordering {last_n} -> {number}')
            last_n = number
            if kind in ('cap', 'error') or row.get('dropped', 0) or row.get('errors', 0):
                issue(f'line {lineno}: probe loss/error ({kind})')
            if kind == 'scope_enter':
                stacks[uint(row['tid'])].append(uint(row['call']))
            elif kind == 'scope_exit':
                stack = stacks[uint(row['tid'])]
                if not stack or stack.pop() != uint(row['call']):
                    issue(f'line {lineno}: scope nesting mismatch')
            elif kind.startswith('wind_'):
                if not wind_covered:
                    raise ValueError('wind event without declared observation coverage')
                call, tid, action_pointer = uint(row['call']), uint(row['tid']), address(row['action'])
                if not action_pointer or not stacks[tid] or stacks[tid][-1] != call:
                    raise ValueError('wind event outside its active scope')
                stack = wind_stacks[tid]
                if kind == 'wind_begin':
                    if call in wind_invocations or address(row['vtable']) != 0x143423898:
                        raise ValueError('duplicate wind invocation or unknown final class')
                    for field, bits in [('owner', 32), ('sequence', 16), ('derived', 8), ('stage', 8)]:
                        uint(row['key'][field], bits)
                    grid = row['grid']
                    if not isinstance(grid, list) or len(grid) != 2:
                        raise ValueError('wind grid must contain two signed coordinates')
                    for coordinate in grid:
                        int32(coordinate)
                    int32(row['heightMarker'])
                    if not address(row['callerRaw']):
                        raise ValueError('null wind caller')
                    caller, frames = row['caller'], row['frames']
                    if not isinstance(frames, list) or len(frames) > 12:
                        raise ValueError('wind frames must be a bounded list')
                    for frame in [caller, *frames]:
                        if frame is not None and not 0x140000000 <= address(frame) < 0x144d8f000:
                            raise ValueError('normalized wind caller/frame outside pinned image')
                    route = {0x1414fc32e: 'scheduled', 0x1414fbdec: 'terminal'}.get(
                        address(caller) if caller is not None else None, 'unknown')
                    wind_counts['route_' + route] += 1
                    wind_invocations.add(call)
                    stack.append(dict(call=call, action=action_pointer, phase=0))
                else:
                    if not stack or stack[-1]['call'] != call or stack[-1]['action'] != action_pointer:
                        raise ValueError('wind invocation nesting/action mismatch')
                    entry = stack[-1]
                    if kind == 'wind_before':
                        pointer = address(row['state'])
                        if entry['phase'] != 0 or not pointer:
                            raise ValueError('duplicate or invalid wind pre-store')
                        entry.update(phase=1, pointer=pointer, before=uint(row['before']))
                    elif kind == 'wind_after':
                        if entry['phase'] != 1 or address(row['state']) != entry['pointer']:
                            raise ValueError('missing wind pre-store or changed state pointer')
                        expected = (entry['before'] * 0x41c64e6d + 0x3039) & 0xffffffff
                        if uint(row['cpuAfter']) != expected or uint(row['after']) != expected:
                            issue(f'line {lineno}: wind CPU/store LCG mismatch')
                        entry['phase'] = 2
                        wind_counts['checked_draws'] += 1
                    elif kind == 'wind_end':
                        if entry['phase'] != 2 or uint(row['draws']) != 1:
                            raise ValueError('incomplete wind inline draw')
                        wind_counts['checked_calls'] += 1
                        stack.pop()
                    else:
                        raise ValueError('unknown wind event')
            elif kind.startswith('context_'):
                if not context_covered:
                    raise ValueError('context event without declared observation coverage')
                call, tid, action_pointer = uint(row['call']), uint(row['tid']), address(row['action'])
                if not action_pointer:
                    raise ValueError('null context action pointer')
                stack = context_stacks[tid]
                if kind == 'context_begin':
                    if call in context_invocations:
                        raise ValueError('duplicate context invocation')
                    if address(row['vtable']) not in CONTEXT_TABLES or not address(row['context']):
                        raise ValueError('unknown context class or null input')
                    for field, bits in [('owner', 32), ('sequence', 16), ('derived', 8), ('stage', 8)]:
                        uint(row['key'][field], bits)
                    expected_parent = stack[-1]['call'] if stack else None
                    if row['parentCall'] is not None:
                        uint(row['parentCall'])
                    if row['parentCall'] != expected_parent:
                        raise ValueError('context parent mismatch')
                    if not address(row['callerRaw']):
                        raise ValueError('null context caller')
                    caller = row['caller']
                    if caller is not None and not 0x140000000 <= address(caller) < 0x144d8f000:
                        raise ValueError('normalized context caller outside pinned image')
                    entry_input = int32(row['input1c'])
                    int32(row['before434'])
                    stack.append(dict(call=call, action=action_pointer, input=entry_input, assigned=None))
                    context_invocations.add(call)
                    context_counts['entries'] += 1
                    context_callers[caller if caller is not None else 'outside-main-image'] += 1
                else:
                    if not stack or stack[-1]['call'] != call or stack[-1]['action'] != action_pointer:
                        raise ValueError('context invocation nesting/action mismatch')
                    entry = stack[-1]
                    if kind == 'context_assign':
                        if entry['assigned'] is not None:
                            raise ValueError('duplicate context assignment')
                        entry['assigned'] = int32(row['assigned434'])
                        context_counts['entry_input_changed'] += int(entry['input'] != entry['assigned'])
                        context_counts['zero_assignments'] += int(entry['assigned'] == 0)
                    elif kind == 'context_end':
                        if entry['assigned'] is None:
                            raise ValueError('missing context assignment observation')
                        after = int32(row['after434'])
                        if after != entry['assigned']:
                            context_counts['assignment_mismatches'] += 1
                            issue(f'line {lineno}: context return differs from observed store value')
                        context_counts['checked_assignments'] += 1
                        stack.pop()
                    else:
                        raise ValueError('unknown context receiver event')
            elif kind.startswith('tls_'):
                if not tls_covered:
                    raise ValueError('TLS event without declared observation coverage')
                call, tid = uint(row['call']), uint(row['tid'])
                stack = tls_stacks[tid]
                if kind in ('tls_chance_begin', 'tls_bits_begin'):
                    if call in tls_invocations or uint(row['scope']) not in stacks[tid]:
                        raise ValueError('duplicate or unscoped TLS invocation')
                    address(row['caller'])
                    entry = dict(kind=kind, call=call, scope=uint(row['scope']),
                                 samples=[], steps=[], value=0, remaining=0)
                    if kind == 'tls_chance_begin':
                        entry['p'] = uint(row['p'])
                    else:
                        bits = uint(row['bits'])
                        if bits > 32:
                            raise ValueError('unsupported TLS bit count')
                        entry['remaining'] = bits
                        parent = stack[-1] if stack else None
                        expected_parent = parent['call'] if parent and parent['kind'] == 'tls_chance_begin' else None
                        if row['chanceCall'] is not None:
                            uint(row['chanceCall'])
                        if row['chanceCall'] != expected_parent:
                            raise ValueError('TLS chance parent mismatch')
                        if expected_parent is not None and (bits != 7 or address(row['caller']) != 0x140e3b48b):
                            raise ValueError('unexpected TLS chance bit-helper call')
                    stack.append(entry)
                    tls_invocations[call] = entry
                elif kind == 'tls_step':
                    if not stack or stack[-1]['call'] != call or stack[-1]['kind'] != 'tls_bits_begin':
                        raise ValueError('unpaired TLS step')
                    entry = stack[-1]
                    if entry['scope'] not in stacks[tid]:
                        raise ValueError('TLS step outlived its observed scope')
                    bits, before = uint(row['bits']), uint(row['before'])
                    pointer = address(row['state'])
                    if not pointer or uint(row['index']) != len(entry['steps']) or not entry['remaining']:
                        raise ValueError('invalid TLS step index/state')
                    if bits != min(15, entry['remaining']):
                        raise ValueError('TLS chunk width mismatch')
                    if entry['steps'] and (pointer != entry['pointer'] or before != entry['after']):
                        raise ValueError('TLS state changed within bit helper')
                    entry['pointer'] = pointer
                    entry['steps'].append(before)
                    entry['after'] = (before * 0x41c64e6d + 0x3039) & 0xffffffff
                    entry['value'] = ((entry['value'] << bits) +
                                      ((entry['after'] >> 16) & ((1 << bits)-1))) & 0xffffffff
                    entry['remaining'] -= bits
                elif kind in ('tls_bits_end', 'tls_chance_end'):
                    expected_kind = kind.replace('_end', '_begin')
                    if not stack or stack[-1]['call'] != call or stack[-1]['kind'] != expected_kind:
                        raise ValueError('TLS invocation nesting mismatch')
                    entry = stack.pop()
                    if entry['scope'] not in stacks[tid]:
                        raise ValueError('TLS invocation outlived its observed scope')
                    if kind == 'tls_bits_end':
                        after = None if row['after'] is None else uint(row['after'])
                        if entry['remaining'] or after != entry.get('after') or uint(row['sample']) != entry['value']:
                            raise ValueError('TLS returned sample/state mismatch or missing steps')
                        tls_counts['checked_bits'] += 1
                        tls_counts['checked_steps'] += len(entry['steps'])
                        if stack and stack[-1]['kind'] == 'tls_chance_begin':
                            parent = stack[-1]
                            if parent['samples'] and (parent['after'] != entry['steps'][0] or
                                                      parent['pointer'] != entry['pointer']):
                                raise ValueError('TLS state changed in chance rejection loop')
                            parent['samples'].append(entry['value'])
                            parent['after'], parent['pointer'] = entry['after'], entry['pointer']
                    else:
                        success = row['success']
                        if type(success) is not bool:
                            raise ValueError('TLS chance result must be boolean')
                        p, samples = entry['p'], entry['samples']
                        if p == 0 or p >= 100:
                            if samples or success != (p >= 100):
                                raise ValueError('TLS no-draw chance boundary mismatch')
                        elif (not samples or any(value < 100 for value in samples[:-1]) or
                              samples[-1] >= 100 or success != (samples[-1] < p)):
                            raise ValueError('TLS chance rejection/result mismatch')
                        tls_counts['checked_chances'] += 1
                        tls_counts['rejected_samples'] += max(0, len(samples)-1)
                else:
                    raise ValueError('unknown TLS record type')
            elif kind == 'rng_init':
                words = row['words']
                if not isinstance(words, list) or len(words) not in (4, 5, 7, 8):
                    raise ValueError('unsupported initializer word count')
                for word in words:
                    uint(word)
                rng = TerminalRandom(words) if len(words) == 8 else ProjectileRandom(words)
                after = state(row['after'])
                rng_counts['checked'] += 1
                if after != [rng.steps, *rng.state]:
                    rng_counts['mismatches'] += 1
                    issue(f'line {lineno}: initializer state mismatch')
                observed_states[row['state']] = after
            elif kind == 'rng_bound':
                before, after = state(row['before']), state(row['after'])
                if row['wordBits'] != 32 or row['mask'] != 0xffffffff:
                    rng_counts['unsupported_wrapper'] += 1
                    continue
                bound = uint(row['bound'])
                if bound == 0:
                    # A uint32 zero cannot be assumed to mean the mathematical 2**32 range.
                    rng_counts['unsupported_wrapper'] += 1
                    continue
                pointer = row['state']
                if pointer in observed_states and observed_states[pointer] != before:
                    rng_counts['unobserved_state_changes'] += 1
                rng = TerminalRandom.__new__(TerminalRandom)
                rng.steps, rng.state = before[0], before[1:]
                sample = rng.bounded(bound)
                rng_counts['checked'] += 1
                if uint(row['sample']) != sample or after != [rng.steps & 0xffffffff, *rng.state]:
                    rng_counts['mismatches'] += 1
                    issue(f'line {lineno}: bounded sample/state mismatch')
                observed_states[pointer] = after
            elif kind == 'object_seed':
                key = row['key']
                expected = object_seed(key['sequence'], key['owner'], key['derived'],
                                       row['newDerived'], row['ordinal'])
                rng_counts['checked'] += 1
                if uint(row['seed']) != expected:
                    rng_counts['mismatches'] += 1
                    issue(f'line {lineno}: object-seed mismatch')
            elif kind == 'result':
                key = result_key(row)
                if ingress_covered:
                    result_snapshot(row)
                    address(row['caller'])
                if uint(row['kind0'], 8) == 0:
                    if arrivals[key]:
                        result_counts['duplicate_arrivals'] += 1
                    arrivals[key] += 1
                    result_counts['queued_arrivals'] += 1
                else:
                    result_counts['immediate_arrivals'] += 1
            elif kind == 'consume':
                key = result_key(row)
                if type(row['found']) is not bool:
                    raise ValueError('found must be boolean')
                if not row['found']:
                    result_counts['missing'] += 1
                elif arrivals[key]:
                    result_counts['success_with_prior_candidate'] += 1
                else:
                    result_counts['success_without_prior_candidate'] += 1
            elif kind.startswith(('result_control_', 'result_process_')):
                if not ingress_covered:
                    raise ValueError('result ingress event without declared observation coverage')
                call, tid = uint(row['call']), uint(row['tid'])
                stack = result_stacks[tid]
                if kind in ('result_control_begin', 'result_process_begin'):
                    if call in result_invocations:
                        raise ValueError('duplicate result invocation ID')
                    actor = address(row['actor'])
                    if not actor:
                        raise ValueError('missing result Actor')
                    caller = address(row['caller'])
                    entry = dict(kind=kind, actor=actor, matched=False, processed=0)
                    if kind == 'result_control_begin':
                        entry.update(key=result_key(row), process=uint(row['processByte'], 8))
                        result_counts['control_requests'] += 1
                        result_counts['control_process_requested' if entry['process']
                                      else 'control_process_zero'] += 1
                        result_counts['control_dispatcher' if caller == 0x1421952bb
                                      else 'control_unknown_caller'] += 1
                    else:
                        identity = result_snapshot(row['before'])
                        pointer = address(row['inputAction'])
                        missing = row['inputActionMissing']
                        if type(missing) is not bool or missing != (pointer == 0):
                            raise ValueError('input action pointer/missing flag disagree')
                        if pointer:
                            action_key = dict(key=row['inputActionKey'], target=identity[4])
                            result_key(action_key)  # Basic identity only; never a projectile-layout assertion.
                        elif row['inputActionKey'] is not None:
                            raise ValueError('null input action has a key')
                        handle = row['statsHandle']
                        if not isinstance(handle, list) or len(handle) != 2:
                            raise ValueError('stats shared handle must contain two pointers')
                        for item in handle:
                            address(item)
                        source = PROCESS_SOURCES.get(caller, 'unknown')
                        if source.startswith('1b0_') and missing != source.endswith('_missing'):
                            raise ValueError('1b0 caller/input action disagree')
                        parent = row['controlCall']
                        if source.startswith('1b0_'):
                            if parent is None or not stack or stack[-1] != uint(parent):
                                raise ValueError('1b0 processing without enclosing control invocation')
                            control = result_invocations[parent]
                            if (control['kind'] != 'result_control_begin' or not control['matched'] or
                                    not control['process'] or control['key'] != identity or control['actor'] != actor):
                                raise ValueError('1b0 processing/control mismatch')
                            control['processed'] += 1
                        elif parent is not None:
                            raise ValueError('non-1b0 processing has a control parent')
                        entry.update(key=identity, flags=row['before']['flags'])
                        result_counts['process_source_' + source] += 1
                        result_counts['process_input_action_missing'] += int(missing)
                    result_invocations[call] = entry
                    stack.append(call)
                else:
                    if not stack or stack[-1] != call:
                        raise ValueError('result invocation nesting mismatch')
                    entry = result_invocations[call]
                    if kind == 'result_control_match':
                        if entry['kind'] != 'result_control_begin' or entry['matched']:
                            raise ValueError('unexpected/duplicate control match')
                        identity = result_snapshot(row['record'])
                        if identity != entry['key']:
                            raise ValueError('control match key mismatch')
                        entry['matched'] = True
                        result_counts['control_match_with_prior_candidate' if arrivals[identity]
                                      else 'control_match_without_prior_candidate'] += 1
                    elif kind == 'result_control_end':
                        if (entry['kind'] != 'result_control_begin' or type(row['matched']) is not bool or
                                entry['matched'] != row['matched']):
                            raise ValueError('control end/match disagree')
                        if entry['processed'] != int(entry['matched'] and entry['process'] != 0):
                            raise ValueError('control processing observation missing/duplicated')
                        label = ('missing' if not entry['matched'] else
                                 'remove_only' if entry['process'] == 0 else 'processed')
                        result_counts['control_' + label] += 1
                        stack.pop()
                    elif kind == 'result_process_end':
                        if entry['kind'] != 'result_process_begin' or type(row['completed']) is not bool:
                            raise ValueError('invalid processing completion')
                        if result_snapshot(row['after']) != entry['key']:
                            raise ValueError('processing changed result identity')
                        result_counts['process_completed' if row['completed'] else 'process_incomplete'] += 1
                        result_counts['process_flags_changed'] += int(entry['flags'] != row['after']['flags'])
                        stack.pop()
                    else:
                        raise ValueError('unknown result observation')
            elif kind == 'skill_stat_update':
                update = uint(row['update'])
                uint(row['owner'])
                uint(row['skillId'], 16)
                uint(row['selector'], 8)
                count = uint(row['pairCount'])
                if type(row['pairsTruncated']) is not bool or not isinstance(row['pairs'], list):
                    raise ValueError('invalid stat pair list/truncation flag')
                pairs = Counter(stat_pair(pair) for pair in row['pairs'])
                if row['pairsTruncated']:
                    issue(f'line {lineno}: skill-stat pair observation truncated')
                elif len(row['pairs']) != count:
                    raise ValueError('skill-stat pair count mismatch')
                if update in updates:
                    raise ValueError('duplicate skill-stat update ID')
                updates[update] = pairs
                active_updates.add(update)
                stat_counts['updates'] += 1
                stat_counts['reported_pairs'] += count
            elif kind == 'skill_stat_apply':
                update, pair = uint(row['update']), stat_pair(row)
                if update not in active_updates or not updates[update][pair]:
                    raise ValueError('stat setter without matching observed update pair')
                updates[update][pair] -= 1
                stats_pointer = address(row['stats'])
                if stats_pointer:
                    updated_pointers.add(stats_pointer)
                stat_counts['applied_pairs'] += 1
            elif kind == 'skill_stat_end':
                update = uint(row['update'])
                if update not in active_updates:
                    raise ValueError('skill-stat dispatch end without active update')
                active_updates.remove(update)
            elif kind == 'stat_view':
                uint(row['call'])
                uint(row['selector'], 8)
                uint(row['skillId'], 16)
                selected = address(row['selected'])
                address(row['rawPrimary'])
                address(row['effectivePrimary'])
                stat_counts['views'] += 1
                if selected in updated_pointers:
                    stat_counts['views_with_prior_pointer_candidate'] += 1
            elif kind == 'animation_request':
                call, tid = uint(row['call']), uint(row['tid'])
                if call not in stacks[tid]:
                    raise ValueError('animation request without enclosing observed scope')
                key = row['key']
                uint(key['owner'])
                sequence = uint(key['sequence'], 16)
                uint(key['derived'], 8)
                uint(key['stage'], 8)
                address(row['request'])
                uint(row['actorSeed'], 16)
                seed, remaining = uint(row['requestSeed']), uint(row['outerRemaining'])
                launch_counts['checked_requests'] += 1
                if seed != ((sequence + remaining) & 0xffffffff):
                    launch_counts['seed_mismatches'] += 1
                    issue(f'line {lineno}: primary animation-request seed mismatch')
                if seed == 0:
                    launch_counts['zero_seed_fallback_candidates'] += 1
            elif kind == 'animation_event':
                key = row['key']
                for field, bits in [('owner', 32), ('sequence', 16), ('derived', 8), ('stage', 8)]:
                    uint(key[field], bits)
                address(row['action'])
                uint(row['eventHash'], 16)
                uint(row['eventType'])
                uint(row['descriptorKind'])
                uint(row['actorFlags'], 8)
                if row['channel'] not in ('normal', 'alternate'):
                    raise ValueError('unknown animation-event channel')
                launch_counts['event_candidates'] += 1
            elif kind == 'end':
                ended = True
        except (ValueError, TypeError, KeyError, OverflowError) as exc:
            issue(f'line {lineno}: malformed record: {exc}')

    close_session()
    if not sessions:
        issue('no projectile capture sessions')
    return dict(sessions=sessions, events=events, complete=bool(sessions) and failures == 0,
                event_types=dict(counts), rng=dict(rng_counts), results=dict(result_counts), stats=dict(stat_counts),
                launch=dict(launch_counts), tls=dict(tls_counts),
                wind=dict(wind_counts),
                wind_observation=dict(covered_sessions=wind_covered_sessions,
                                      uncovered_sessions=sessions-wind_covered_sessions),
                context=dict(context_counts), context_callers=dict(context_callers),
                context_observation=dict(covered_sessions=context_covered_sessions,
                                         uncovered_sessions=sessions-context_covered_sessions),
                result_observation=dict(covered_sessions=covered_sessions,
                                        uncovered_sessions=sessions - covered_sessions),
                diagnostic_count=failures, diagnostics=diagnostics,
                limitations=['Static arithmetic comparison; no server/client parity claim.',
                             'Prior result arrivals are candidates; not all queue consumers are hooked.',
                             'Result processing completion is not damage; null input action can be resolved internally.',
                             'Unobserved RNG state changes may be other consumers or pointer reuse.',
                             'Stat pointer matches are candidates, not value propagation or cache refresh proof.',
                             'Animation requests/events do not prove clip admission or projectile release.',
                             'Context entry inputs can change before assignment; caller observations do not establish a wire field.',
                             'Zero context events in a covered session do not prove that no producer exists.',
                             'Wind draws are checked independently; bounded callers/frames do not close outer TLS history.',
                             'Wind rotation observations do not establish child geometry, final orientation or damage.'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('jsonl')
    args = parser.parse_args()
    with open(args.jsonl, encoding='utf-8-sig') as source:
        result = analyze(source)
    print(json.dumps(result, indent=2))
    return 0 if result['complete'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
