"""Dump a Unity scene's GameObject hierarchy + components (read-only inspection helper)."""
import re
import sys

path = sys.argv[1]
show_all = '--all' in sys.argv
lines = open(path, encoding='utf-8', errors='replace').read().split('\n')

docs = {}
cur_id = None
cur_class = None
cur_lines = []
for ln in lines:
    m = re.match(r'^--- !u!(\d+) &(\d+)', ln)
    if m:
        if cur_id is not None:
            docs[cur_id] = (cur_class, cur_lines)
        cur_id = int(m.group(2))
        cur_class = int(m.group(1))
        cur_lines = []
    elif cur_id is not None:
        cur_lines.append(ln)
if cur_id is not None:
    docs[cur_id] = (cur_class, cur_lines)


def field(ls, name):
    for l in ls:
        m = re.match(r'^  ' + re.escape(name) + r':\s*(.*)$', l)
        if m:
            return m.group(1).strip()
    return None


def ref(txt):
    if not txt:
        return None
    m = re.search(r'fileID: (\d+)', txt)
    return int(m.group(1)) if m else None


CLASS = {1: 'GameObject', 4: 'Transform', 114: 'MonoBehaviour', 224: 'RectTransform',
         54: 'Rigidbody', 65: 'BoxCollider', 136: 'CapsuleCollider', 135: 'SphereCollider',
         64: 'MeshCollider', 33: 'MeshFilter', 23: 'MeshRenderer', 20: 'Camera', 108: 'Light',
         1001: 'PrefabInstance', 1002: 'PrefabInstanceTransform', 218: 'Terrain',
         82: 'AudioSource', 198: 'ParticleSystem', 208: 'ParticleSystemRenderer'}

gobjs = {}
for fid, (cls, ls) in docs.items():
    if cls == 1:
        gobjs[fid] = {
            'name': field(ls, 'm_Name'),
            'components': [int(x) for x in re.findall(r'component: \{fileID: (\d+)\}', '\n'.join(ls))],
            'active': field(ls, 'm_IsActive'),
        }

trs = {}
for fid, (cls, ls) in docs.items():
    if cls in (4, 224):
        kids = []
        in_children = False
        for l in ls:
            if re.match(r'^  m_Children:', l):
                in_children = True
                continue
            if in_children:
                m = re.match(r'^  \{fileID: (\d+)\}', l)
                if m:
                    kids.append(int(m.group(1)))
                elif re.match(r'^  \w', l):
                    in_children = False
        trs[fid] = {'go': ref(field(ls, 'm_GameObject')), 'father': ref(field(ls, 'm_Father')),
                    'children': kids, 'pos': field(ls, 'm_LocalPosition'),
                    'rot': field(ls, 'm_LocalRotation'), 'scale': field(ls, 'm_LocalScale')}

comp_class = {fid: CLASS.get(cls, 'cls%d' % cls) for fid, (cls, ls) in docs.items()}
comp_script = {}
for fid, (cls, ls) in docs.items():
    if cls == 114:
        m = re.search(r'guid: ([0-9a-f]+)', field(ls, 'm_Script') or '')
        comp_script[fid] = m.group(1) if m else None

# prefab instance name overrides
prefab_names = {}
for fid, (cls, ls) in docs.items():
    if cls == 1001:
        for i, l in enumerate(ls):
            if re.match(r'^    m_Name:', l):
                prefab_names[fid] = l.split(':', 1)[1].strip()


def goname(fid):
    if fid is None or fid not in gobjs:
        return '(?)'
    return gobjs[fid]['name'] or '(unnamed)'


def comps_of(go):
    out = []
    if go not in gobjs:
        return out
    for cfid in gobjs[go]['components']:
        if cfid not in comp_class:
            continue
        cn = comp_class[cfid]
        if cn == 'MonoBehaviour':
            cn = 'MB:' + (comp_script.get(cfid) or '?')[:8]
        out.append(cn)
    return out


roots = [fid for fid, t in trs.items() if t['father'] is None or t['father'] not in trs]
out = []


def dump(tfid, depth):
    t = trs[tfid]
    go = t['go']
    name = goname(go)
    if show_all or name not in ('(unnamed)',):
        act = '' if (go in gobjs and gobjs[go]['active'] == '1') else ' [INACTIVE]'
        out.append('  ' * depth + '- %s%s   {%s}  pos=%s' %
                   (name, act, ', '.join(comps_of(go)), t['pos']))
        depth += 1
    for c in t['children']:
        if c in trs:
            dump(c, depth)


for r in roots:
    dump(r, 0)
print('\n'.join(out))
