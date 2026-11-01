# Exact shared Split candidate ordering (3.29.3b)

> Frozen detailed reference from R81. Historical next-step language is provenance, not an active backlog.
> Current scope and unresolved obligations are in [the delivery matrix](delivery-matrix.md) and
> [verification and limitations](verification-and-limitations.md).

Part of [RE 012](../012-projectile-execution.md) and
[continuation/memory](continuation-and-memory.md#10-shared-split-targets-first-then-fills-missing-directions).
This recovers the selected sort computation, not spatial admission or authoritative damage.
Target SHA256: `78a0e227189c263658a7f65a02f086428b859bfc114ae8e7ce8aab721ebd48d5`.
Evidence is original-file data and offline instruction interpretation; no client was executed.

## 1. Concrete caller and record contract

Shared Split emitter `0x141bae540` collects candidates, then calls sorter `0x141bbc1f0`
at `0x141bae769`. Its argument setup at `0x141bae74f..0x141bae761` supplies the candidate
begin/end pointers and initial budget `I = (end-begin)/16`, the **candidate count**, not the requested
number of children. The later emission loop consumes candidates from the sorted beginning.

Each record occupies 16 bytes:

| Offset | Field | Sort meaning |
|---|---|---|
| `+0` | byte: seen in current history phase | unsigned primary key; actual constructed boolean is 0 or 1 |
| `+1..3` | padding | not a comparison input or payload copied by the selected moves |
| `+4` | uint32: query-center squared distance | unsigned secondary key |
| `+8` | target pointer | payload moved with the keys, never compared or dereferenced by these four bodies |

`less(a,b)` means unsigned lexicographic comparison of `(seen,distance)`.
Neither target ID nor pointer value is a tie-breaker. Seen candidates remain eligible after unseen candidates;
sorting is not an extra exclusion pass. The query/filter/history meaning remains in the parent chapter.

The selected sort family is closed over four direct-call targets:

| Entry | Operation | Original instructions |
|---|---|---:|
| `0x141bbc1f0` | threshold/budget dispatch, recursion, insertion and heap loops | 181 |
| `0x141bbc490` | median selection and three-way partition | 265 |
| `0x141bbc7f0` | heap hole adjustment | 96 |
| `0x141bbc960` | ordered three-record comparisons/swaps | 63 |

There are no RNG calls in this family. A transported opaque byte is forwarded through several calls;
the selected comparisons still use only the two record keys. The Split emitter initializes its separate
eight-word fallback-angle context later, at `0x141bae820`.
Exact sorting therefore changes which target receives a child ordinal without spending an angle draw itself.

## 2. Threshold, recursion and budget

For a valid half-open record range `[first,last)`, let `n=last-first` in records.

```text
Sort(first,last,I):
    while last-first > 32:
        if I <= 0: HeapSort(first,last); return
        (pl,pr) = Partition(first,last)
        I = floor(I/2) + floor(I/4)
        if pl-first < last-pr:
            Sort(first,pl,I)
            first = pr
        else:
            Sort(pr,last,I)
            last = pl
    StrictLessInsertionSort(first,last)
```

The two integer shifts happen separately. For example, budget 3 becomes 1, not `floor(3*3/4)=2`.
The smaller outer partition recurses; equal outer lengths recurse right and continue left.
The equal-key middle `[pl,pr)` needs no recursion.
Threshold 32 applies to each current subrange, including after a larger root has already permuted it.
Insertion moves only strictly greater records, so an untouched root of at most 32 candidates preserves
encounter order among equal keys. This does not restore global stability after earlier partition swaps.

## 3. Exact pivot preparation and partition rotations

All positions below are record indices. `mid=first+floor(n/2)` and `end=last-1`.
`Median3(x,y,z)` performs these comparisons **in order**:

```text
if less(A[y],A[x]): swap(x,y)
if less(A[z],A[y]):
    swap(y,z)
    if less(A[y],A[x]): swap(x,y)
```

For `n<=41`, prepare `Median3(first,mid,end)`.
For `n>=42`, set `s=floor(n/8)` and prepare the following four triples in order:

```text
Median3(first, first+s, first+2*s)
Median3(mid-s, mid, mid+s)
Median3(end-2*s, end-s, end)
Median3(first+s, mid, end-s)
```

This threshold comes from comparing `n-1` with 40 at `0x141bbc4d4`.
Preparation can move equal-key payloads relative to other records even though each swap uses strict less.

`Partition` then executes the following record-level algorithm; equality means equality of the two keys.
It is not interchangeable with an arbitrary library partition that returns the same key groups.

```text
pl=mid; pr=mid+1
while first<pl and key(A[pl-1])==key(A[pl]): pl-=1
while pr<last and key(A[pr])==key(A[pl]): pr+=1
left=pl; right=pr
loop:
    while right<last:
        if less(A[pl],A[right]): right+=1; continue
        if less(A[right],A[pl]): break
        if pr!=right: swap(pr,right)
        pr+=1; right+=1
    while first<left:
        pos=left-1
        if less(A[pos],A[pl]): left-=1; continue
        if less(A[pl],A[pos]): break
        pl-=1
        if pl!=pos: swap(pl,pos)
        left-=1
    if left==first and right==last: return (pl,pr)
    if left==first:
        if pr!=right: swap(pl,pr)
        pr+=1; swap(pl,right); pl+=1; right+=1
    else if right==last:
        left-=1; pl-=1
        if left!=pl: swap(left,pl)
        pr-=1; swap(pl,pr)
    else:
        left-=1; swap(left,right); right+=1
```

The two exhausted-side rotations are distinct. Replacing them with stable grouping or adding a random tie-break
changes payload positions, which can change both target selection and target-to-ordinal association.

## 4. Exact heap adjustment

Heap fallback first adjusts holes from `floor(n/2)-1` down through zero, carrying each original hole record.
It then repeatedly carries the last record, moves the root to that last slot, shortens the heap by one,
and adjusts hole zero. Indices in the following adjustment are relative to that heap's first record:

```text
Adjust(hole,n,value):
    top=hole; limit=floor((n-1)/2)
    while hole<limit:
        left=2*hole+1; right=left+1
        child = right if not less(A[right],A[left]) else left
        A[hole]=A[child]; hole=child
    if hole==limit and n is even:
        A[hole]=A[n-1]; hole=n-1
    while top<hole:
        parent=floor((hole-1)/2)
        if not less(A[parent],value): break
        A[hole]=A[parent]; hole=parent
    A[hole]=value
```

Equal children select the **right** child (`SETAE` at `0x141bbc85a`).
The carried record is not compared on the downward pass; it bubbles back only while the parent is strictly less.
Using a generic heap implementation with a different equal-child policy does not preserve payload order.

## 5. A normal-budget tie witness

For 33 synthetic records labelled `j=0..32`, use `seen=j%2`, `distance=j%5` and initial budget 33.
The recovered original sorter returns this beginning:

```text
[0,10,20,30,16,26,6,2,12,22,32,...]
```

The `(seen=0,distance=1)` group is `16,26,6`; stable sorting would give `6,16,26`.
With five requested children, the fifth target would consequently be label 16 rather than label 6.
Even if all candidates are used, equal-key payloads can be associated with different emitted ordinals.
Later angle draws still depend on selected/current coordinates: missing or center-coincident aims take
the parent's fallback path. The absence of RNG inside sorting does not make downstream draw scheduling unconditional.
This is a constructed record-array witness, not a claim about occurrence rate or a specific official map.
An all-equal 33-record array happens to retain order; that special case does not establish general stability.

## 6. Verification and remaining boundary

Round27 `split_sort_model.py` implements the record-level contract above independently of instruction decoding.
`split_sort_machine.py` interprets the original four bodies in bounded synthetic array/stack memory,
with width-specific signed/unsigned flags and direct calls restricted to this family.
It never executes the executable or enters its runtime/provider.

`check_split_sort.py` compares 1,687 cases: 138 fixed/threshold/budget fixtures, 1,093 exhaustive short arrays
over three keys and 456 mixed/tied/unsigned-distance arrays. All final record permutations and 1,036
partition snapshots match through 6,253,328 interpreted instructions. All 605 sort instruction addresses
are exercised. Explicit zero-budget fixtures cover heap behavior; they do not prove that a particular official
root reaches budget exhaustion. Normal test roots use the actual candidate-count budget.

`review_split_sort.py/json` separately redecodes the four complete bodies, checks all direct edges,
37 semantic anchors including the caller, fourteen literal expected controls and 27 median truth-table cases.
It checks frozen artifact hashes but shares the same integer interpreter; it is not independent native execution.
The separate context-input exploration only extended an initialization stat-pair publisher and did not close
the speed-context producer. That gap stays open.

The sort is now recoverable for valid candidate arrays. Spatial encounter/mutation order, path/layer admission,
history updates before collection and actual target coordinates when emission later reads them remain separate.
The computation is useful static port evidence; it does not identify official server sort code or establish
authoritative damage quotas, a local hit-report packet or successful end-to-end synchronization.
