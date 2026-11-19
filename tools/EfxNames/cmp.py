import json,sys
c=json.load(open('cs.json')); e=json.load(open('ext.json'))
SZ=dict(Single=4,ExpressionAssignType=4,UInt32=4,CenterSpread=8,Color=4,Range=8,UndeterminedFieldType=4,Vector3=12,Int32=4,Boolean=1,MinExtraI=8,Byte=1,Vector2=8,RangeI=8,RotationOrder=4,MinMaxI=8,MinMax=8,InnerOuter=8,AxisXYZ=4,LifeType=4,MinMaxExclusiveI=8,StartSpan=8,Int3=12,MinExtra=8,CenterSpreadI=8,EmitterColorOperator=4,PlayType=4,PlayOrder=4,AnchorPair=8,AxisType=4,PtColorOperator=4,Int16=2,Int4=16,SortType=4,Repeat=4,VelocityType=4,ClampRange=8,Shape2DType=4,Shape3DType=4,RotationCorrectType=4,DistortionType=4,PtLifeStatus=4,LuminanceBleedType=4,LuminanceBleedSamplingType=4,Finish=4,Vector4=16,VelocityShapeType=4,EffectBoundsType=4,NodeBillboardType=4,VelocityEmitType=4,VelocityAttenuationType=4,SolidBodyShapeType=4,TerrainSnapType=4)
def cssize(f):
    s=0
    for n,t in f:
        if t not in SZ: return None
        s+=SZ[t]
    return s
cw={x['id']:x for x in c['MHWilds']}
rows=[]
for i,x in sorted(cw.items()):
    w=e['wilds'].get(str(i))
    cs=cssize(x['fields'])
    ex=w['load'].get('payload') if w else None
    rows.append((i,x['kind'],cs,ex,w['install'] if w else None, w['load']['kind'] if w else '-'))
mism=[r for r in rows if r[2] is not None and r[3] is not None and r[2]!=r[3]]
print('size compared',sum(1 for r in rows if r[2] is not None and r[3] is not None),'mismatch',len(mism))
for r in mism: print('  ',r)
print('exe range but C# variable/unknown', [(r[0],r[1],r[3]) for r in rows if r[2] is None and r[3] is not None])
print('custom loaders',[(r[0],r[1]) for r in rows if r[5]=='custom'])
