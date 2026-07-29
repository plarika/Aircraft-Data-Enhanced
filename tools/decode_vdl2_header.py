# SPDX-License-Identifier: MIT
import numpy as np, math, json
from pathlib import Path
SYMBOL_RATE=10500.0; ALPHA=.6; TP=128; PRE=16; MAXSEARCH=256; HDR=25; LFSR=0x6959
GRAY=(0,1,3,2,6,7,5,4)
EXP=np.array((0,3,-3,1,1,2,0,4,-3,4,-2,3,1,-2,-3,0),float)*math.pi/4
MASKS=(int('0000000011111111111110000',2),int('0011111100001111111101000',2),int('1100011100110000111100100',2),int('1101101101010011001100010',2),int('0110100111100101010100001',2))
def ma(v,w): return np.convolve(v,np.ones(w)/w,'same')
def bursts(iq,sr):
 p=np.abs(iq)**2;s=ma(p,max(8,round(sr*.0015))); n=max(float(np.quantile(s,.3)),1e-20); th=n*4;bth=n*2;gap=max(1,round(sr*.002));mn=max(16,round(sr*.008));mx=max(mn,round(sr*1.2));guard=max(8,round(sr*.008));segs=[];st=last=None
 for i,a in enumerate(s>th):
  if a:
   if st is None: st=i
   last=i
  elif st is not None and i-last>gap:
   segs.append((st,last+1));st=last=None
 if st is not None:segs.append((st,last+1))
 out=[]
 for st,en in segs:
  if not mn<=en-st<=mx or st<guard or en>len(s)-guard:continue
  lead=float(s[st-guard:st].mean());trail=float(s[en:en+guard].mean());sig=float(s[st:en].mean())
  snr=10*math.log10(max(sig/n,1e-12));edge=10*math.log10(max(sig/max(lead,trail),1e-12))
  if lead>bth or trail>bth or snr<5 or edge<3:continue
  ex=max(1,round(sr*.002));out.append((max(guard,st-ex),min(len(iq)-guard,en+ex),snr))
 return out
def rrc(sr):
 sps=sr/SYMBOL_RATE; half=math.ceil(10*sps/2); ts=[]
 for i in range(-half,half+1):
  t=i/sps
  if abs(t)<1e-12:v=1+ALPHA*(4/math.pi-1)
  elif abs(abs(t)-1/(4*ALPHA))<1e-8:v=ALPHA/math.sqrt(2)*((1+2/math.pi)*math.sin(math.pi/(4*ALPHA))+(1-2/math.pi)*math.cos(math.pi/(4*ALPHA)))
  else:
   num=math.sin(math.pi*t*(1-ALPHA))+4*ALPHA*t*math.cos(math.pi*t*(1+ALPHA));den=math.pi*t*(1-(4*ALPHA*t)**2);v=0 if abs(den)<1e-12 else num/den
  ts.append(v)
 ts=np.array(ts); return ts/math.sqrt(float(np.sum(ts**2)))
def sample(x,st,en,off,sps):
 pos=np.arange(st+off,en-1,sps);idx=np.floor(pos).astype(int);f=pos-idx;return x[idx]*(1-f)+x[idx+1]*f
def evalpre(sy,tpi,off,si):
 ph=np.angle(sy[si:si+PRE]);er=np.unwrap(ph-EXP);ix=np.arange(PRE,dtype=float);cen=ix-ix.mean();slope=float(np.dot(cen,er-er.mean())/np.dot(cen,cen));inter=float(np.mean(er-slope*ix));res=np.angle(np.exp(1j*(er-(inter+slope*ix))));rms=float(np.sqrt(np.mean(res**2)));corr=float(abs(np.mean(np.exp(1j*res))));return dict(tpi=tpi,off=off,si=si,rms=rms,corr=corr,slope=slope,sy=sy,metric=rms+.05*(1-corr))
def findpre(x,st,en,sr):
 sps=sr/SYMBOL_RATE;best=None
 for tpi in range(TP):
  off=tpi/TP*sps;sy=sample(x,st,en,off,sps)
  if len(sy)<PRE:continue
  for si in range(min(MAXSEARCH,len(sy)-PRE)+1):
   c=evalpre(sy,tpi,off,si)
   if best is None or c['metric']<best['metric']:best=c
 return best
def demod(c):
 sy=c['sy'];first=c['si']+PRE;bits=[]
 for i in range(first,len(sy)):
  dp=(np.angle(sy[i])-np.angle(sy[i-1])-c['slope'])%(2*math.pi);sector=round(dp/(math.pi/4))%8;v=GRAY[sector];bits += [(v>>2)&1,(v>>1)&1,v&1]
 return bits
def descr(bits):
 l=LFSR;out=[]
 for b in bits:
  fb=((l>>0)^(l>>14))&1;l=(l>>1)|(fb<<14);out.append(b^fb)
 return out
def word(bits,n):
 v=0
 for b in bits[:n]:v=(v<<1)|(b&1)
 return v
def syndrome(h):return sum((((h&m).bit_count()&1)<<r) for r,m in enumerate(MASKS))
def rev(v,n):
 r=0
 for _ in range(n):r=(r<<1)|(v&1);v>>=1
 return r
def hdr(bits):
 d=descr(bits); raw=word(d,25); h=raw&((1<<22)-1); sb=syndrome(h);c=[]
 if sb:
  for p in range(25):
   z=h^(1<<p)
   if syndrome(z)==0:c.append((p,z))
 cor=h;bit=-1
 if len(c)==1: p,cor=c[0];bit=24-p
 sa=syndrome(cor);fec=sa==0 and (sb==0 or len(c)==1);reserved=(cor>>22)&7;length=rev((cor>>5)&0x1ffff,17);valid=fec and reserved==0 and length>0 and length<= (0x1fff if sb else 0x3fff)
 return dict(valid=valid,sb=sb,sa=sa,len=length,hex=f'{cor:07X}',corr=len(c)==1,bit=bit,raw=''.join(map(str,bits[:25])),desc=''.join(map(str,d[:25])))
def main():
 import argparse
 ap=argparse.ArgumentParser(description='Decode VDL2 preamble and 25-bit physical header from IQ.')
 ap.add_argument('iq')
 ap.add_argument('--sample-rate',type=float,default=37500.0)
 ap.add_argument('--output')
 args=ap.parse_args()
 p=Path(args.iq).resolve();raw=np.fromfile(p,'<f4')
 if raw.size%2: raise SystemExit('Invalid IQ file: odd float count.')
 iq=raw[::2]+1j*raw[1::2];iq-=iq.mean();sr=args.sample_rate
 bs=bursts(iq,sr);f=np.convolve(iq,rrc(sr),'same');analyses=[]
 for idx,b in enumerate(bs):
  c=findpre(f,b[0],b[1],sr)
  if c is None:
   analyses.append({'burst_index':idx,'status':'no_preamble'});continue
  pre_ok=c['rms']<=.42 and c['corr']>=.91
  item={'burst_index':idx,'start_sample':b[0],'end_sample':b[1],
        'start_ms':b[0]/sr*1000,'duration_ms':(b[1]-b[0])/sr*1000,
        'estimated_snr_db':b[2],'timing_phase_index':c['tpi'],
        'timing_offset_samples':c['off'],'preamble_symbol_index':c['si'],
        'preamble_rms_deg':math.degrees(c['rms']),'preamble_correlation':c['corr'],
        'residual_frequency_offset_hz':c['slope']*SYMBOL_RATE/(2*math.pi)}
  if not pre_ok:
   item['status']='preamble_metric_rejected';analyses.append(item);continue
  bits=demod(c); header=hdr(bits); item['raw_bit_count']=len(bits);item['raw_bit_preview']=''.join(map(str,bits[:192]));item['header']=header;item['status']='VDL2-HEADER-VALID' if header['valid'] else ('header_fec_failed' if header['sa'] else 'header_length_invalid');analyses.append(item)
 result={'schema_version':1,'stage':'vdl2_frame_sync_and_header','iq_file':p.name,'sample_rate':sr,'bounded_burst_count':len(bs),'analyses':analyses}
 out=Path(args.output).resolve() if args.output else p.with_suffix('.vdl2-header.json');out.write_text(json.dumps(result,indent=2),encoding='utf-8');print(json.dumps(result,indent=2));print(f'\nSaved: {out}')
if __name__=='__main__': main()
