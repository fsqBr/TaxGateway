# PKI de TESTE (sem nenhum valor fora dos testes)

Raiz e certificado e-CNPJ fictícios usados para assinar o corpus golden de XML (D-311).
A raiz só é confiável quando a configuração `Xml:ExtraTrustAnchorPemFiles` aponta para ela, o que acontece
apenas nos testes. Em produção as únicas âncoras são as raízes ICP-Brasil embutidas (D-302).
Senha do PFX: `teste`.
