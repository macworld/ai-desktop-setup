#!/usr/bin/env python3
"""Publish an exact, accepted candidate via a complete draft; never replace assets."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import urllib.request
from package import inventory


def require_immutable(response):
    if response.get('enabled') is not True:raise ValueError('Release immutability must be enabled')


def immutable_settings(repository):
    token=os.environ.get('RELEASE_SETTINGS_TOKEN')
    if not token:raise ValueError('Read-only repository settings credential required')
    request=urllib.request.Request('https://api.github.com/repos/'+repository+'/immutable-releases',headers={'Authorization':'Bearer '+token,'Accept':'application/vnd.github+json','X-GitHub-Api-Version':'2026-03-10'})
    # Never forward this credential to a redirect or any other endpoint.
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self,*args,**kwargs):return None
    try:
        with urllib.request.build_opener(NoRedirect).open(request,timeout=30) as response:
            if response.status != 200:raise ValueError('Unexpected immutable-settings HTTP status')
            require_immutable(json.load(response))
    except Exception:
        raise ValueError('Immutable release settings unavailable or disabled') from None


def gh(*args,input=None):
    env={k:v for k,v in os.environ.items() if k!='RELEASE_SETTINGS_TOKEN'};env['GH_HOST']='github.com'
    result=subprocess.run(['gh',*args],input=input,text=True,capture_output=True,env=env)
    if result.returncode:raise ValueError('GitHub release operation failed; inspect draft/run state')
    return result.stdout


def api(path,method='GET',body=None):
    args=['api','--hostname','github.com',path,'--method',method]
    if body is not None:args+=['--input','-']
    return json.loads(gh(*args,input=json.dumps(body) if body is not None else None))


def tag_commit(repository,tag):
    obj=api('repos/'+repository+'/git/ref/tags/'+tag)['object']
    for _ in range(8):
        if obj['type']=='commit':return obj['sha']
        if obj['type']!='tag' or not re.fullmatch('[0-9a-f]{40}',obj['sha']):break
        obj=api('repos/'+repository+'/git/tags/'+obj['sha'])['object']
    raise ValueError('Unsupported tag target')


def validate_assets(assets,expected):
    if len(assets)!=len(expected) or {a['name'] for a in assets}!=set(expected):raise ValueError('Incomplete or duplicate draft attachments')
    for a in assets:
        data=expected[a['name']]
        if a['size']!=len(data) or a.get('digest')!='sha256:'+hashlib.sha256(data).hexdigest():raise ValueError('Draft asset digest mismatch')


def publish(candidate,bundle,bundle_sha256,repository,tag,commit):
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+',repository) or not re.fullmatch(r'v[0-9]+\.[0-9]+\.[0-9]+',tag) or not re.fullmatch('[0-9a-f]{40}',commit):raise ValueError('Invalid release identity')
    candidate,bundle=Path(candidate),Path(bundle)
    if bundle.is_symlink() or hashlib.sha256(bundle.read_bytes()).hexdigest()!=bundle_sha256:raise ValueError('Provenance bundle changed')
    expected=inventory(candidate)
    required={f'AI-Desktop-Setup-{tag[1:]}-{a}.exe' for a in ('x64','arm64')}|{'release-manifest.json','verification.json','SHA256SUMS','candidate-index.json','signing-requests.json','nsis-3.12-src.tar.bz2'}
    if set(expected)!=required:raise ValueError('Exact complete candidate attachment set required')
    expected['provenance.sigstore.json']=bundle.read_bytes()
    manifest=json.loads(expected['release-manifest.json'])
    if manifest['source_commit']!=commit or manifest['source_repository']!=repository or manifest['tag']!=tag:raise ValueError('Manifest identity mismatch')
    immutable_settings(repository)
    if tag_commit(repository,tag)!=commit:raise ValueError('Tag changed before draft creation')
    base='repos/'+repository+'/releases'
    # API rejects an existing published tag. Explicitly reject existing drafts too.
    releases=json.loads(gh('api','--hostname','github.com',base+'?per_page=100','--paginate','--slurp'))
    if any(r['tag_name']==tag for page in releases for r in page):raise ValueError('Release already exists; never overwrite')
    draft=api(base,'POST',dict(tag_name=tag,target_commitish=commit,name=tag,body='Verified nested signed installers. See attached provenance and verification.',draft=True,prerelease=False))
    release_path=base+'/'+str(int(draft['id']))
    gh('release','upload',tag,*[str(candidate/n) for n in sorted(required)],str(bundle),'--repo',repository)
    state=api(release_path)
    if state['draft'] is not True or state['tag_name']!=tag:raise ValueError('Draft identity changed')
    validate_assets(state['assets'],expected)
    with tempfile.TemporaryDirectory() as tmp:
        gh('release','download',tag,'--repo',repository,'--dir',tmp,'--pattern','*')
        if inventory(Path(tmp))!=expected:raise ValueError('Downloaded draft bytes differ')
    if tag_commit(repository,tag)!=commit:raise ValueError('Tag changed before publication')
    immutable_settings(repository)
    published=api(release_path,'PATCH',{'draft':False,'make_latest':'true'})
    if published.get('draft') is not False or published.get('immutable') is not True:raise ValueError('Published immutability postcondition failed; inspect release immediately')
    validate_assets(published['assets'],expected)
    if tag_commit(repository,tag)!=commit:raise ValueError('Published tag changed')


def main():
    p=argparse.ArgumentParser(description=__doc__)
    for n in ('candidate','bundle','bundle-sha256','repository','tag','commit'):p.add_argument('--'+n,required=True)
    a=p.parse_args();publish(a.candidate,a.bundle,a.bundle_sha256,a.repository,a.tag,a.commit)

if __name__=='__main__':main()
