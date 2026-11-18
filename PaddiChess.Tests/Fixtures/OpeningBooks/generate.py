#!/usr/bin/env python3
"""Regenerate synthetic book fixtures using the independent pinned openbook reference.
Usage: python3 generate.py /path/to/liujh168-openbook
Reference commit: 33e02fc9cccb4e6c4f45029d707d3074090f9107 (GPL-3.0-or-later).
No production database or private game history is included in these fixtures.
"""
import sys,sqlite3,struct,random,json,math
from pathlib import Path
if len(sys.argv) != 2: raise SystemExit(__doc__)
sys.path.insert(0,str(Path(sys.argv[1]).resolve()))
import convert_obk as ref
rng=random.Random(141592)
keys=ref.load_jqxq_randoms()
def fen(board):
    rows=[]
    for r in range(10):
        s=''; n=0
        for p in board.cells[r*9:r*9+9]:
            if p=='.':n+=1
            else:
                if n:s+=str(n);n=0
                s+=p
        if n:s+=str(n)
        rows.append(s)
    return '/'.join(rows)+(' b' if board.side else ' w')
def ee_key(board):
    state=list(range(256)); j=0
    for i in range(256):j=(j+state[i])%256;state[i],state[j]=state[j],state[i]
    x=y=0
    def nxt():
        nonlocal x,y
        x=(x+1)%256;y=(y+state[x])%256;state[x],state[y]=state[y],state[x];return state[(state[x]+state[y])%256]
    raw=bytes(nxt() for _ in range((1+14*256)*12))
    t=[struct.unpack_from('<I',raw,i*12+8)[0] for i in range(1+14*256)]
    k=t[0] if board.side else 0
    for i,p in enumerate(board.cells):
        if p!='.':k^=t[1+(ref.OBK_TYPES[p.lower()]+(7 if p.islower() else 0))*256+((i//9+3)*16+i%9+3)]
    return k
initial=ref.Board.from_fen(ref.START)
b1=initial.apply((7*9+7,7*9+4))
b2=b1.apply((2*9+1,2*9+4))
boards=[initial, ref.Board(initial.cells,1), b1,b2]
moves=['h2e2','b7e7','b7e7','b0c2']
for _ in range(30000):
    b=['.']*90;b[4]='k';b[85]='K';b[40]='P'
    for p in 'RNCPrncp':
        i=rng.randrange(90)
        while b[i]!='.':i=rng.randrange(90)
        b[i]=p
    candidate=ref.Board(tuple(b),0)
    if math.isnan(struct.unpack('<d',struct.pack('<Q',ref.obk_key(candidate)))[0]):
        boards.append(candidate);moves.append('a0a1');break
assert len(boards)==5
out=Path(__file__).resolve().parent
cases=[]
for b,m in zip(boards,moves):
    cases.append(dict(fen=fen(b),move=m,binghe=f'{ref.obk_key(b):016X}',eleeye=f'{ee_key(b):08X}',jqxq=f'{ref.jqxq_key(b,keys):016X}'))
(out/'formats.json').write_text(json.dumps(cases,indent=2)+'\n')
def enc(m,high=True):
    a=(12-int(m[1]))*16+ord(m[0])-ord('a')+3;b=(12-int(m[3]))*16+ord(m[2])-ord('a')+3
    return (a<<8|b) if high else (b<<8|a)
for mode,name in [('eleeye','eleeye.dat'),('jqxq','jqxq.bin')]:
    entries=[]
    for i,c in enumerate(cases):entries.append((int(c[mode],16),enc(c['move'],mode=='jqxq'),100+i))
    entries.sort()
    (out/name).write_bytes(b''.join(struct.pack('<QHHI',*e,0) if mode=='jqxq' else struct.pack('<IHH',*e) for e in entries))
# Add duplicates, invalid/disabled high-score moves, multi-page index and overflow memo.
entries=[(int(c['binghe'],16),enc(c['move']),100+i,1,'test') for i,c in enumerate(cases)]
entries += [(int(cases[0]['binghe'],16),enc('a0a9'),9999,1,'illegal'),(int(cases[0]['binghe'],16),enc('b0c2'),9998,0,'disabled')]
entries[0]=(*entries[0][:4],'long memo '*600)
entries += [(rng.getrandbits(64),enc('a0a1'),0,0,'') for i in range(1400)]
def var(b,pos):
    v=0
    for i in range(9):
        x=b[pos];pos+=1
        if i==8:return v*256+x,pos
        v=v*128+(x&127)
        if not x&128:return v,pos

def build(name,numeric=False,nan_index=False):
    data=[e for e in entries if nan_index or not numeric or not math.isnan(struct.unpack('<d',struct.pack('<Q',e[0]))[0])]
    def order(k):
        d=struct.unpack('<d',struct.pack('<Q',k))[0]
        return (0,next(i for i,e in enumerate(data) if e[0]==k)) if math.isnan(d) else (1,d if k>>63 else k)
    ordered=sorted(set(e[0] for e in data),key=order if numeric else None)
    mapping={k:0x1000000000000000+i for i,k in enumerate(ordered)};reverse={v:k for k,v in mapping.items()}
    p=out/name
    if p.exists():p.unlink()
    c=sqlite3.connect(p);c.execute('pragma page_size=512')
    c.execute('create table bhobk(id integer primary key, vkey integer, vmove integer, vscore integer, vwin integer, vdraw integer, vlost integer, vvalid integer, vmemo blob, vindex integer)')
    c.executemany('insert into bhobk(vkey,vmove,vscore,vwin,vdraw,vlost,vvalid,vmemo,vindex) values(?,?,?,1,2,3,?,?,0)',[(mapping[k],m,s,valid,memo) for k,m,s,valid,memo in data])
    c.execute('create index idxkey on bhobk(vkey)');c.commit();c.close()
    b=bytearray(p.read_bytes());size=512
    for page in range(len(b)//size):
        start=page*size;h=start+(100 if page==0 else 0);kind=b[h]
        if kind not in [2,10,13]:continue
        count=int.from_bytes(b[h+3:h+5],'big')
        for i in range(count):
            ptr=h+(12 if kind==2 else 8)+i*2;at=start+int.from_bytes(b[ptr:ptr+2],'big')
            if kind==2:at+=4
            plenpos=at;plen,at=var(b,at)
            if kind==13:_,at=var(b,at)
            hlen,serial=var(b,at);fielddata=at+hlen;field=0
            while serial<at+hlen:
                serloc=serial;t,serial=var(b,serial)
                n=([0,1,2,3,4,6,8,8,0,0][t] if t<10 else (t-12)//2)
                if t==6 and field==(1 if kind==13 else 0):
                    old=int.from_bytes(b[fielddata:fielddata+n],'big')
                    if old in reverse:
                        k=reverse[old]
                        if nan_index and kind in [2,10] and math.isnan(struct.unpack('<d',struct.pack('<Q',k))[0]):
                            assert plen < 128
                            b[serloc]=0
                            rec=b[at:fielddata]+b[fielddata+8:at+plen]
                            b[plenpos]=plen-8;b[at:at+len(rec)]=rec
                            break
                        b[serloc]=7 if k>>63 else 6;b[fielddata:fielddata+n]=k.to_bytes(8,'big')
                fielddata+=n;field+=1
    p.write_bytes(b)
build('binghe.obk');build('binghe-sqlite.obk',True);build('binghe-nan-index.obk',True,True)
print([(p.name,p.stat().st_size) for p in out.glob('*')])
print(json.dumps(cases,indent=2))
