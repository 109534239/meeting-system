-- ============================================================
-- Certificatecategories 補充種子資料 v4（PostgreSQL 版本）
-- 用途：擴充「國外／專業」證照選項，涵蓋 IT雲端資安、專案管理、
--       財務金融保險、人資、行銷、語言檢定、設計等領域，
--       共 36 筆，已排除跟您現有資料（含自訂的 MOS/Adobe/PMP/AWS/CCNA/Oracle 等）重複的項目。
--
-- ⚠️ 這份不是官方公告名單，是業界常見證照的整理清單（跟 v1 那 22 筆國外證照性質一樣），
--    純粹擴充選項豐富度，之後想再擴充直接照同樣格式加就好。
-- ============================================================

INSERT INTO "Certificatecategories" ("CertCode", "CertName", "AvailableLevels") VALUES
('', 'Google Cloud Associate Cloud Engineer', ''),
('', 'Google Cloud Professional Cloud Architect', ''),
('', 'Microsoft Certified: Azure Administrator (AZ-104)', ''),
('', 'CompTIA A+', ''),
('', 'CompTIA Security+', ''),
('', 'CompTIA Network+', ''),
('', 'Cisco CCNP', ''),
('', 'Red Hat Certified Engineer (RHCE)', ''),
('', 'Certified Kubernetes Administrator (CKA)', ''),
('', 'Salesforce Certified Administrator', ''),
('', 'SAP Certified Application Associate', ''),
('', 'ITIL Foundation', ''),
('', 'Certified Information Systems Auditor (CISA)', ''),
('', 'PRINCE2 Foundation', ''),
('', 'CAPM 助理專案管理師', ''),
('', 'Lean Six Sigma', 'Yellow Belt/Green Belt/Black Belt'),
('', '記帳士', ''),
('', '地政士', ''),
('', '不動產經紀人', ''),
('', '人身保險業務員', ''),
('', '財產保險業務員', ''),
('', '證券商業務員', ''),
('', '期貨交易分析人員', ''),
('', '信託業業務人員', ''),
('', '理財規劃人員', ''),
('', 'PHR 人力資源專業認證', ''),
('', 'SPHR 資深人力資源專業認證', ''),
('', 'HubSpot Inbound Marketing 認證', ''),
('', 'Meta Blueprint 認證', ''),
('', 'Facebook Blueprint 認證', ''),
('', '全民英檢 GEPT', '初級/中級/中高級/高級'),
('', '華語文能力測驗 TOCFL', ''),
('', '漢語水平考試 HSK', 'HSK1/HSK2/HSK3/HSK4/HSK5/HSK6'),
('', '韓國語文能力測驗 TOPIK', 'TOPIK1/TOPIK2'),
('', 'Adobe Certified Professional', ''),
('', 'Autodesk Certified User (AutoCAD)', '');