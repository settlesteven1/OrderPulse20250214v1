-- ============================================================================
-- OrderPulse Database Schema
-- Migration 003: Seed common retailers
-- ============================================================================

INSERT INTO [dbo].[Retailers] ([RetailerId], [Name], [NormalizedName], [SenderDomains], [WebsiteUrl], [ReturnPolicyDays], [ReturnPolicyNotes])
VALUES
    (NEWID(), 'Amazon', 'amazon',
     '["amazon.com","amazon.co.uk","amazon.ca","amazon.com.au","email.amazon.com","shipment-tracking.amazon.com"]',
     'https://www.amazon.com', 30, 'Most items returnable within 30 days. Some categories have extended or restricted windows.'),

    (NEWID(), 'Walmart', 'walmart',
     '["walmart.com","email.walmart.com","e.walmart.com"]',
     'https://www.walmart.com', 90, 'Most items returnable within 90 days. Electronics within 30 days.'),

    (NEWID(), 'Target', 'target',
     '["target.com","em.target.com","e.target.com"]',
     'https://www.target.com', 90, 'Most items returnable within 90 days. RedCard holders get 120 days. Electronics within 30 days.'),

    (NEWID(), 'Best Buy', 'best buy',
     '["bestbuy.com","emailinfo.bestbuy.com"]',
     'https://www.bestbuy.com', 15, 'Standard 15-day return window. My Best Buy Elite/Elite Plus members get 30/45 days.'),

    (NEWID(), 'Apple', 'apple',
     '["apple.com","email.apple.com","orders.apple.com","gc.apple.com"]',
     'https://www.apple.com', 14, '14-day return window from delivery date. Products must be undamaged.'),

    (NEWID(), 'Nike', 'nike',
     '["nike.com","info.nike.com","email.nike.com"]',
     'https://www.nike.com', 60, '60-day return window. Items must be unworn and unwashed. Nike Members get free returns.'),

    (NEWID(), 'Adidas', 'adidas',
     '["adidas.com","info.adidas.com","email.adidas.com"]',
     'https://www.adidas.com', 30, '30-day return window for unworn, unwashed items with original tags.'),

    (NEWID(), 'Costco', 'costco',
     '["costco.com","w2.costco.com"]',
     'https://www.costco.com', NULL, 'Satisfaction guarantee with no time limit on most items. Electronics have 90-day policy.'),

    (NEWID(), 'Home Depot', 'home depot',
     '["homedepot.com","email.homedepot.com"]',
     'https://www.homedepot.com', 90, 'Most items within 90 days. Some appliances within 48 hours.'),

    (NEWID(), 'Lowe''s', 'lowes',
     '["lowes.com","email.lowes.com"]',
     'https://www.lowes.com', 90, '90-day return window for most items. Major appliances within 48 hours.'),

    (NEWID(), 'Etsy', 'etsy',
     '["etsy.com","mail.etsy.com","transaction.etsy.com"]',
     'https://www.etsy.com', NULL, 'Return policies vary by seller. Check individual shop policies.'),

    (NEWID(), 'eBay', 'ebay',
     '["ebay.com","reply.ebay.com","rover.ebay.com"]',
     'https://www.ebay.com', 30, 'eBay Money Back Guarantee covers most purchases. Return windows vary by seller.'),

    (NEWID(), 'Nordstrom', 'nordstrom',
     '["nordstrom.com","e.nordstrom.com"]',
     'https://www.nordstrom.com', NULL, 'No time limit on returns. Evaluated case by case. Free returns.'),

    (NEWID(), 'Zara', 'zara',
     '["zara.com","e.zara.com"]',
     'https://www.zara.com', 30, '30-day return window. Items must have tags and be in original condition.'),

    (NEWID(), 'H&M', 'h&m',
     '["hm.com","email.hm.com","e.hm.com"]',
     'https://www.hm.com', 30, '30-day return window. H&M members get 60 days. Items must be unused with tags.'),

    (NEWID(), 'Gap', 'gap',
     '["gap.com","e.gap.com","email.gap.com"]',
     'https://www.gap.com', 30, '30-day return window for unworn, unwashed items. Online purchases returnable by mail or in-store.'),

    (NEWID(), 'Wayfair', 'wayfair',
     '["wayfair.com","email.wayfair.com"]',
     'https://www.wayfair.com', 30, '30-day return window. Buyer pays return shipping. Some items non-returnable.'),

    (NEWID(), 'Chewy', 'chewy',
     '["chewy.com","mail.chewy.com"]',
     'https://www.chewy.com', 365, '365-day return window. Exceptional customer service - often issues refunds without requiring return shipment.'),

    (NEWID(), 'Newegg', 'newegg',
     '["newegg.com","info.newegg.com","email.newegg.com"]',
     'https://www.newegg.com', 30, '30-day return window. Some items have 15-day or non-returnable policies. Check product page.'),

    (NEWID(), 'B&H Photo', 'b&h photo',
     '["bhphoto.com","bhphotovideo.com","email.bhphoto.com"]',
     'https://www.bhphotovideo.com', 30, '30-day return window. Items must be in original condition with all accessories.');
GO

PRINT 'Seeded 20 common retailers.';
GO
