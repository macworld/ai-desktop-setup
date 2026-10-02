"""Synthetic framing only: these fixtures must never certify native trust."""
import importlib.util
from pathlib import Path
import struct
import unittest

class SignedPeTests(unittest.TestCase):
    def mod(self):
        p=Path(__file__).with_name('signed_pe.py');self.assertTrue(p.exists(),'strict certificate framing missing')
        s=importlib.util.spec_from_file_location('signed_pe',p);m=importlib.util.module_from_spec(s);s.loader.exec_module(m);return m
    def fixture(self):
        d=bytearray(520);d[:2]=b'MZ';struct.pack_into('<I',d,60,128);d[128:132]=b'PE\0\0'
        struct.pack_into('<HH',d,132,0x14c,1);struct.pack_into('<H',d,148,224)
        struct.pack_into('<H',d,152,0x10b);struct.pack_into('<I',d,212,512);struct.pack_into('<I',d,244,16)
        struct.pack_into('<II',d,280,512,8);struct.pack_into('<IHH',d,512,8,0x200,2)
        return d
    def test_framing_does_not_claim_trust(self):
        result=self.mod().framing(bytes(self.fixture()),require_signed=True)
        self.assertEqual(result['certificate_count'],1);self.assertNotIn('signature_valid',result)
    def test_bad_certificate_tables_rejected(self):
        m=self.mod()
        for offset,value,fmt in [(280,0,'I'),(284,0,'I'),(280,513,'I'),(284,16,'I'),(512,7,'I'),(512,9,'I'),(516,0x100,'H'),(518,1,'H'),(148,100,'H'),(244,4,'I'),(132,0x8664,'H')]:
            d=self.fixture();struct.pack_into('<'+fmt,d,offset,value)
            with self.subTest(offset=offset,value=value),self.assertRaises(ValueError):m.framing(bytes(d),require_signed=True)
        for data in (self.fixture()[:140],self.fixture()+b'x'):
            with self.assertRaises(ValueError):m.framing(bytes(data),require_signed=True)
    def test_unsigned_and_changed_signing_body_rejected(self):
        m=self.mod();unsigned=self.fixture()[:512];struct.pack_into('<II',unsigned,280,0,0)
        with self.assertRaises(ValueError):m.framing(bytes(unsigned),require_signed=True)
        signed=self.fixture();signed[400]=1
        with self.assertRaises(ValueError):m.compare_signing(bytes(unsigned),bytes(signed),outer=True)
    def test_zero_alignment_and_exact_signing_mutation(self):
        m=self.mod();unsigned=self.fixture()[:512];struct.pack_into('<II',unsigned,280,0,0)
        m.compare_signing(bytes(unsigned),bytes(self.fixture()),outer=True)
        signed=self.fixture()+b'\0'*8;struct.pack_into('<I',signed,284,16);signed[-1]=1
        with self.assertRaises(ValueError):m.framing(bytes(signed),require_signed=True)
