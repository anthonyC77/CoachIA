---
paths:
  - "front/src/**/*.ts"
---
# Angular — les règles qui surprennent
- Specs : `describe('Cn …')` référence le critère ; pas de `HttpClient` réel, uniquement `HttpClientTestingModule` ou les fakes de `front/src/testing/`.
- Interdits en spec : `fdescribe`, `fit`, `xit` laissés dans le diff.
- Un composant n'appelle jamais l'API directement : passage par un service typé de `front/src/app/api/` (types générés depuis les contrats back, pas écrits à la main).
